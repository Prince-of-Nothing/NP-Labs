package main

import (
	"bufio"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"strings"
	"sync"
	"syscall"
	"time"
)

type Peer struct {
	Name     string
	Endpoint string
	LastSeen time.Time
}

type Msg struct {
	T     string `json:"t"`
	Name  string `json:"name,omitempty"`
	Ep    string `json:"ep,omitempty"`
	From  string `json:"from,omitempty"`
	To    string `json:"to,omitempty"`
	Text  string `json:"text,omitempty"`
	Nonce string `json:"nonce,omitempty"`
	Tag   string `json:"tag,omitempty"`
	Ct    string `json:"ct,omitempty"`
	ID    int    `json:"id,omitempty"`
}

var (
	userName     string
	chatPort     int
	privEndpoint string
	passphrase   string
	key          []byte
	genConn      *net.UDPConn
	privConn     *net.UDPConn
	peers        = map[string]*Peer{}
	peersMu      sync.Mutex
	seenGen      = map[string]bool{}
	seenMu       sync.Mutex
	logMu        sync.Mutex
	logFile      *os.File
	running      = true
	msgCounter   int
)

func logLine(s string) {
	fmt.Println(s)
}

func logDg(dir, endpoint, kind string, size int) {
	logMu.Lock()
	defer logMu.Unlock()
	if logFile != nil {
		fmt.Fprintf(logFile, "%s %s %s %s %d\n", time.Now().Format(time.RFC3339Nano), dir, endpoint, kind, size)
	}
}

func listenUDP(port int) (*net.UDPConn, error) {
	lc := net.ListenConfig{Control: func(network, address string, c syscall.RawConn) error {
		var serr error
		if err := c.Control(func(fd uintptr) { serr = setReuse(fd) }); err != nil {
			return err
		}
		return serr
	}}
	pc, err := lc.ListenPacket(context.Background(), "udp4", fmt.Sprintf("0.0.0.0:%d", port))
	if err != nil {
		return nil, err
	}
	return pc.(*net.UDPConn), nil
}

func localIP() string {
	// Prefer the address of the interface the OS would use for the LAN. This
	// works even when a sandbox (Android proot) hides the interface list.
	if c, err := net.Dial("udp4", "8.8.8.8:80"); err == nil {
		if a, ok := c.LocalAddr().(*net.UDPAddr); ok && a.IP != nil && !a.IP.IsUnspecified() && !a.IP.IsLoopback() {
			ip := a.IP.String()
			c.Close()
			return ip
		}
		c.Close()
	}
	ifaces, _ := net.Interfaces()
	for _, iface := range ifaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, _ := iface.Addrs()
		for _, a := range addrs {
			if ipnet, ok := a.(*net.IPNet); ok {
				if ip := ipnet.IP.To4(); ip != nil {
					return ip.String()
				}
			}
		}
	}
	return "127.0.0.1"
}

func broadcastTargets() []net.IP {
	seen := map[string]bool{}
	var out []net.IP
	ifaces, _ := net.Interfaces()
	for _, iface := range ifaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, _ := iface.Addrs()
		for _, a := range addrs {
			ipnet, ok := a.(*net.IPNet)
			if !ok {
				continue
			}
			ip := ipnet.IP.To4()
			if ip == nil || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
				continue
			}
			b := make(net.IP, 4)
			for i := 0; i < 4; i++ {
				b[i] = ip[i] | ^ipnet.Mask[i]
			}
			if !seen[b.String()] {
				seen[b.String()] = true
				out = append(out, b)
			}
		}
	}
	if !seen[net.IPv4bcast.String()] {
		out = append(out, net.IPv4bcast)
	}
	// A broadcast sent out the network card does not come back to the other
	// peers on this computer; the loopback broadcast does.
	loop := net.IPv4(127, 255, 255, 255)
	if !seen[loop.String()] {
		out = append(out, loop)
	}
	return out
}

func broadcast(m Msg) {
	data, err := json.Marshal(m)
	if err != nil || len(data) > 2000 {
		return
	}
	// Send on every interface: a multi-homed host (Wi-Fi + Hyper-V/VPN) would
	// otherwise send 255.255.255.255 out the wrong one and never reach the LAN.
	for _, ip := range broadcastTargets() {
		addr := &net.UDPAddr{IP: ip, Port: chatPort}
		n, err := genConn.WriteToUDP(data, addr)
		logDg("SEND", addr.String(), m.T, n)
		if err != nil {
			logLine("send error: " + err.Error())
		}
	}
}

// endpointFromSource replaces the advertised address with the address the
// datagram actually came from, keeping the advertised private port. A host
// with several network cards may advertise the wrong interface in "ep".
func endpointFromSource(ep string, src *net.UDPAddr) string {
	if ep == "" || src == nil {
		return ep
	}
	_, port, err := net.SplitHostPort(ep)
	if err != nil {
		return ep
	}
	if src.IP == nil || src.IP.IsUnspecified() {
		return ep
	}
	return net.JoinHostPort(src.IP.String(), port)
}

func sendPrivate(endpoint string, m Msg) {
	data, err := json.Marshal(m)
	if err != nil {
		return
	}
	addr, err := net.ResolveUDPAddr("udp4", endpoint)
	if err != nil {
		logLine("sendpriv error: " + err.Error())
		return
	}
	n, err := privConn.WriteToUDP(data, addr)
	logDg("SEND", addr.String(), m.T, n)
	if err != nil {
		logLine("sendpriv error: " + err.Error())
	}
}

func addPeer(name, ep string) {
	peersMu.Lock()
	defer peersMu.Unlock()
	if p, ok := peers[name]; ok {
		p.Endpoint = ep
		p.LastSeen = time.Now()
		return
	}
	peers[name] = &Peer{Name: name, Endpoint: ep, LastSeen: time.Now()}
	logLine("* " + name + " joined the chat")
}

func touchPeer(name, ep string) {
	peersMu.Lock()
	defer peersMu.Unlock()
	if p, ok := peers[name]; ok {
		p.LastSeen = time.Now()
	} else {
		peers[name] = &Peer{Name: name, Endpoint: ep, LastSeen: time.Now()}
		logLine("* " + name + " joined the chat")
	}
}

func removePeer(name string) {
	peersMu.Lock()
	defer peersMu.Unlock()
	if _, ok := peers[name]; ok {
		delete(peers, name)
		logLine("* " + name + " left the chat")
	}
}

func recvGeneral() {
	buf := make([]byte, 4096)
	for running {
		n, addr, err := genConn.ReadFromUDP(buf)
		if err != nil {
			if running {
				logLine("recvgen error: " + err.Error())
			}
			continue
		}
		logDg("RECV", addr.String(), "general", n)
		if n > 2000 {
			logDg("DROP", addr.String(), "too_long", n)
			continue
		}
		var m Msg
		if err := json.Unmarshal(buf[:n], &m); err != nil {
			logDg("DROP", addr.String(), "invalid_json", n)
			continue
		}
		switch m.T {
		case "join":
			if m.Name == userName && m.Ep == privEndpoint {
				continue // own loopback
			}
			if m.Name == userName {
				sendPrivate(endpointFromSource(m.Ep, addr), Msg{T: "nameinuse", Name: m.Name})
				continue
			}
			addPeer(m.Name, endpointFromSource(m.Ep, addr))
			broadcast(Msg{T: "hello", Name: userName, Ep: privEndpoint})
		case "hello":
			if m.Name == userName {
				continue
			}
			addPeer(m.Name, endpointFromSource(m.Ep, addr))
		case "bye":
			if m.Name == userName {
				continue
			}
			removePeer(m.Name)
		case "heartbeat":
			if m.Name == userName {
				continue
			}
			touchPeer(m.Name, endpointFromSource(m.Ep, addr))
		case "gen":
			if len(m.Text) > 500 {
				logDg("DROP", addr.String(), "gen_too_long", n)
				continue
			}
			if m.From == userName {
				continue
			}
			if m.ID != 0 {
				k := fmt.Sprintf("%s:%d", m.From, m.ID)
				seenMu.Lock()
				if seenGen[k] {
					seenMu.Unlock()
					continue
				}
				seenGen[k] = true
				seenMu.Unlock()
			}
			logLine("[general] " + m.From + ": " + m.Text)
		default:
			logDg("DROP", addr.String(), "unknown_type", n)
		}
	}
}

func recvPrivate() {
	buf := make([]byte, 4096)
	for running {
		n, addr, err := privConn.ReadFromUDP(buf)
		if err != nil {
			if running {
				logLine("recvpriv error: " + err.Error())
			}
			continue
		}
		logDg("RECV", addr.String(), "private", n)
		var m Msg
		if err := json.Unmarshal(buf[:n], &m); err != nil {
			logDg("DROP", addr.String(), "invalid_json", n)
			continue
		}
		switch m.T {
		case "priv":
			if len(m.Text) > 500 {
				logDg("DROP", addr.String(), "priv_too_long", n)
				continue
			}
			logLine("[private] " + m.From + ": " + m.Text)
		case "privenc":
			if key == nil {
				logLine("[private] " + m.From + ": <encrypted, no passphrase>")
				continue
			}
			nonce, e1 := base64.StdEncoding.DecodeString(m.Nonce)
			tag, e2 := base64.StdEncoding.DecodeString(m.Tag)
			ct, e3 := base64.StdEncoding.DecodeString(m.Ct)
			if e1 != nil || e2 != nil || e3 != nil {
				logLine("[private] " + m.From + ": <decryption failed>")
				continue
			}
			block, err := aes.NewCipher(key)
			if err != nil {
				logLine("[private] " + m.From + ": <decryption failed>")
				continue
			}
			gcm, err := cipher.NewGCM(block)
			if err != nil {
				logLine("[private] " + m.From + ": <decryption failed>")
				continue
			}
			plain, err := gcm.Open(nil, nonce, append(ct, tag...), nil)
			if err != nil {
				logLine("[private] " + m.From + ": <decryption failed>")
				continue
			}
			if len(plain) > 500 {
				logDg("DROP", addr.String(), "privenc_too_long", n)
				continue
			}
			logLine("[private] " + m.From + ": " + string(plain))
		case "nameinuse":
			logLine("ERROR: name '" + m.Name + "' already in use")
			os.Exit(1)
		default:
			logDg("DROP", addr.String(), "unknown_private", n)
		}
	}
}

func sendPrivateMessage(endpoint, to, text string) {
	if key == nil {
		sendPrivate(endpoint, Msg{T: "priv", From: userName, To: to, Text: text})
		return
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		logLine("encrypt error: " + err.Error())
		return
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		logLine("encrypt error: " + err.Error())
		return
	}
	nonce := make([]byte, gcm.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		logLine("encrypt error: " + err.Error())
		return
	}
	sealed := gcm.Seal(nil, nonce, []byte(text), nil) // ciphertext || tag
	ct := sealed[:len(sealed)-gcm.Overhead()]
	tag := sealed[len(sealed)-gcm.Overhead():]
	sendPrivate(endpoint, Msg{
		T: "privenc", From: userName, To: to,
		Nonce: base64.StdEncoding.EncodeToString(nonce),
		Tag:   base64.StdEncoding.EncodeToString(tag),
		Ct:    base64.StdEncoding.EncodeToString(ct),
	})
}

func heartbeatLoop() {
	for running {
		time.Sleep(5 * time.Second)
		if !running {
			break
		}
		broadcast(Msg{T: "heartbeat", Name: userName, Ep: privEndpoint})
		now := time.Now()
		var gone []string
		peersMu.Lock()
		for name, p := range peers {
			if now.Sub(p.LastSeen) > 15*time.Second {
				gone = append(gone, name)
			}
		}
		for _, name := range gone {
			delete(peers, name)
			logLine("* " + name + " left the chat")
		}
		peersMu.Unlock()
	}
}

func main() {
	args := os.Args[1:]
	if len(args) < 2 {
		fmt.Println("Usage: chat <name> <port> [passphrase]")
		return
	}
	userName = args[0]
	fmt.Sscanf(args[1], "%d", &chatPort)
	if len(args) >= 3 {
		passphrase = args[2]
	}
	if passphrase != "" {
		k, err := pbkdf2.Key(sha256.New, passphrase, make([]byte, 16), 100000, 32)
		if err != nil {
			fmt.Println("key derivation error:", err)
			return
		}
		key = k
	}

	f, err := os.OpenFile(fmt.Sprintf("chat-%s-%d.log", userName, chatPort), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err == nil {
		logFile = f
		defer f.Close()
	}

	genConn, err = listenUDP(chatPort)
	if err != nil {
		fmt.Println("bind error:", err)
		return
	}
	defer genConn.Close()

	privConn, err = listenUDP(0)
	if err != nil {
		fmt.Println("priv bind error:", err)
		return
	}
	defer privConn.Close()
	privEndpoint = fmt.Sprintf("%s:%d", localIP(), privConn.LocalAddr().(*net.UDPAddr).Port)

	go recvGeneral()
	go recvPrivate()
	go heartbeatLoop()

	broadcast(Msg{T: "join", Name: userName, Ep: privEndpoint})
	time.Sleep(1200 * time.Millisecond)
	logLine(fmt.Sprintf("* %s joined (private ep %s). Type messages, /msg name text, /users, /quit", userName, privEndpoint))

	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		switch {
		case line == "/quit":
			broadcast(Msg{T: "bye", Name: userName})
			running = false
			return
		case line == "/users":
			fmt.Println("Online:")
			peersMu.Lock()
			for _, p := range peers {
				fmt.Printf("  %s %s\n", p.Name, p.Endpoint)
			}
			peersMu.Unlock()
		case strings.HasPrefix(line, "/msg "):
			rest := line[5:]
			sp := strings.Index(rest, " ")
			if sp < 0 {
				logLine("usage: /msg name text")
				continue
			}
			target := rest[:sp]
			text := rest[sp+1:]
			if len(text) > 500 {
				logLine("message too long (max 500)")
				continue
			}
			peersMu.Lock()
			p, ok := peers[target]
			peersMu.Unlock()
			if !ok {
				logLine("ERROR: " + target + " not online")
				continue
			}
			sendPrivateMessage(p.Endpoint, target, text)
			logLine("[private] you -> " + target + ": " + text)
		default:
			if len(line) > 500 {
				logLine("message too long (max 500)")
				continue
			}
			msgCounter++
			broadcast(Msg{T: "gen", From: userName, Text: line, ID: msgCounter})
			logLine("[general] " + userName + ": " + line)
		}
	}
	running = false
}
