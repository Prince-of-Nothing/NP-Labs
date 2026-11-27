package main

import (
	"encoding/json"
	"flag"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"
)

type Message struct {
	ID     int64     `json:"id"`
	Topic  string    `json:"topic"`
	Sender string    `json:"sender"`
	Body   string    `json:"body"`
	Time   time.Time `json:"time"`
}

// Topic holds the messages of one topic. mu protects its fields. pubMu makes one
// publish at a time: take the id, store the message, write the file and queue the
// delivery all happen under it, so the ids, the file and the delivery order agree.
type Topic struct {
	Name        string
	NextID      int64
	Messages    []Message
	Subscribers map[chan Message]string // open answers -> client address
	mu          sync.Mutex
	pubMu       sync.Mutex
}

type Broker struct {
	dataDir    string
	tokensFile string
	mu         sync.Mutex // protects topics
	topics     map[string]*Topic

	// Published messages waiting for delivery, in publish order. A publish waits
	// here only when the deliveries are far behind (backpressure).
	deliver chan Message
}

// validTopic: letters, digits, '-' and '_', 1 to 64 characters. A topic name is
// used as a directory name, so nothing else is allowed.
var validTopic = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

func NewBroker(dataDir, tokensFile string) *Broker {
	b := &Broker{
		dataDir:    dataDir,
		tokensFile: tokensFile,
		topics:     map[string]*Topic{},
		deliver:    make(chan Message, 1024),
	}
	os.MkdirAll(dataDir, 0755)
	b.load()
	go b.deliveryLoop()
	return b
}

func (b *Broker) load() {
	entries, _ := os.ReadDir(b.dataDir)
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		name := e.Name()
		path := filepath.Join(b.dataDir, name, "messages.json")
		t := &Topic{Name: name, Subscribers: map[chan Message]string{}, NextID: 1}
		if data, err := os.ReadFile(path); err == nil {
			var msgs []Message
			if json.Unmarshal(data, &msgs) == nil {
				t.Messages = msgs
				for _, m := range msgs {
					if m.ID >= t.NextID {
						t.NextID = m.ID + 1
					}
				}
			}
		}
		b.topics[name] = t
	}
}

func (b *Broker) topic(name string) *Topic {
	b.mu.Lock()
	defer b.mu.Unlock()
	t, ok := b.topics[name]
	if !ok {
		t = &Topic{Name: name, Subscribers: map[chan Message]string{}, NextID: 1}
		b.topics[name] = t
		os.MkdirAll(filepath.Join(b.dataDir, name), 0755)
	}
	return t
}

// persist writes the topic's messages to messages.json through a temporary file,
// so a crash in the middle of a write leaves the old file in place. The caller
// holds t.mu, so the file always holds the latest state and two writes for one
// topic never overlap.
func (b *Broker) persist(t *Topic) error {
	data, err := json.Marshal(t.Messages)
	if err != nil {
		return err
	}
	path := filepath.Join(b.dataDir, t.Name, "messages.json")
	tmp := path + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	if _, err := f.Write(data); err != nil {
		f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		f.Close()
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	return os.Rename(tmp, path) // replaces the old file on Linux and on Windows
}

// deliveryLoop writes the published messages, one at a time and in publish order,
// to the open answers of each topic. Every subscriber therefore sees one topic's
// messages in id order.
func (b *Broker) deliveryLoop() {
	for msg := range b.deliver {
		b.mu.Lock()
		t := b.topics[msg.Topic]
		b.mu.Unlock()
		if t == nil {
			continue
		}
		t.mu.Lock()
		subs := make(map[chan Message]string, len(t.Subscribers))
		for ch, client := range t.Subscribers {
			subs[ch] = client
		}
		t.mu.Unlock()
		for ch, client := range subs {
			select {
			case ch <- msg:
				log.Printf("Delivered id=%d topic=%s to %s", msg.ID, msg.Topic, client)
			default:
				// the subscriber stopped reading: forget it; the others still get the message
				t.mu.Lock()
				if _, ok := t.Subscribers[ch]; ok {
					delete(t.Subscribers, ch)
					close(ch)
				}
				t.mu.Unlock()
				log.Printf("Subscriber removed (too slow) topic=%s client=%s", msg.Topic, client)
			}
		}
	}
}

type TokenInfo struct {
	Publisher string
	Topics    map[string]bool
}

func (b *Broker) token(tok string) *TokenInfo {
	data, err := os.ReadFile(b.tokensFile)
	if err != nil {
		return nil
	}
	for _, line := range strings.Split(string(data), "\n") {
		// tolerate a UTF-8 BOM written by Windows editors (Notepad, PowerShell)
		line = strings.TrimPrefix(line, "\uFEFF")
		parts := strings.Fields(line)
		if len(parts) < 3 {
			continue
		}
		if parts[0] == tok {
			ti := &TokenInfo{Publisher: parts[1], Topics: map[string]bool{}}
			for _, t := range strings.Split(parts[2], ",") {
				ti.Topics[t] = true
			}
			return ti
		}
	}
	return nil
}

func (b *Broker) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	path := strings.TrimSuffix(r.URL.Path, "/")
	log.Printf("%s %s from %s", r.Method, path, r.RemoteAddr)

	if r.Method == "GET" && path == "/topics" {
		b.mu.Lock()
		out := make([]map[string]interface{}, 0, len(b.topics))
		for name, t := range b.topics {
			t.mu.Lock()
			out = append(out, map[string]interface{}{"topic": name, "size": len(t.Messages)})
			t.mu.Unlock()
		}
		b.mu.Unlock()
		writeJSON(w, 200, out)
		return
	}

	rest := strings.TrimPrefix(path, "/topics/")
	if r.Method == "POST" && strings.HasPrefix(path, "/topics/") && strings.HasSuffix(rest, "/messages") {
		name := strings.TrimSuffix(rest, "/messages")
		if !validTopic.MatchString(name) {
			log.Printf("400 invalid topic name %q from %s", name, r.RemoteAddr)
			w.WriteHeader(400)
			return
		}
		b.publish(w, r, name)
		return
	}

	if r.Method == "GET" && strings.HasPrefix(path, "/topics/") && strings.HasSuffix(rest, "/stream") {
		name := strings.TrimSuffix(rest, "/stream")
		if !validTopic.MatchString(name) {
			log.Printf("400 invalid topic name %q from %s", name, r.RemoteAddr)
			w.WriteHeader(400)
			return
		}
		b.stream(w, r, name)
		return
	}

	log.Printf("404 %s %s from %s", r.Method, path, r.RemoteAddr)
	http.NotFound(w, r)
}

func (b *Broker) publish(w http.ResponseWriter, r *http.Request, topicName string) {
	// A token from the tokens file is always required. With no tokens file no
	// token can match, so every publish is refused (fail closed).
	auth := r.Header.Get("Authorization")
	if !strings.HasPrefix(strings.ToLower(auth), "bearer ") {
		w.Header().Set("WWW-Authenticate", "Bearer")
		w.WriteHeader(401)
		log.Printf("Publish unauthorized (no token) %s topic=%s", r.RemoteAddr, topicName)
		return
	}
	ti := b.token(strings.TrimSpace(auth[7:]))
	if ti == nil {
		w.Header().Set("WWW-Authenticate", "Bearer")
		w.WriteHeader(401)
		log.Printf("Publish unauthorized (unknown token, or no tokens file) %s topic=%s", r.RemoteAddr, topicName)
		return
	}
	if !ti.Topics[topicName] {
		w.WriteHeader(403)
		log.Printf("Publish forbidden %s topic=%s", r.RemoteAddr, topicName)
		return
	}

	// The sender is the token's publisher; a "sender" field in the body is ignored.
	var in struct {
		Sender string `json:"sender"`
		Body   string `json:"body"`
	}
	body, _ := io.ReadAll(r.Body)
	if err := json.Unmarshal(body, &in); err != nil {
		w.WriteHeader(400)
		log.Printf("400 invalid json from %s topic=%s", r.RemoteAddr, topicName)
		return
	}

	t := b.topic(topicName)
	t.pubMu.Lock() // one publish at a time per topic: ids, the file and the delivery order agree
	t.mu.Lock()
	msg := Message{ID: t.NextID, Topic: topicName, Sender: ti.Publisher, Body: in.Body, Time: time.Now().UTC()}
	t.Messages = append(t.Messages, msg)
	if err := b.persist(t); err != nil {
		// not saved, so not accepted: take the message back out
		t.Messages = t.Messages[:len(t.Messages)-1]
		t.mu.Unlock()
		t.pubMu.Unlock()
		w.WriteHeader(500)
		log.Printf("Publish failed (not saved) topic=%s from %s: %v", topicName, r.RemoteAddr, err)
		return
	}
	t.NextID++
	t.mu.Unlock()
	b.deliver <- msg // waits here only when the deliveries are far behind
	t.pubMu.Unlock()

	writeJSON(w, 202, msg)
	log.Printf("Publish accepted id=%d topic=%s from %s", msg.ID, topicName, r.RemoteAddr)
}

func (b *Broker) stream(w http.ResponseWriter, r *http.Request, topicName string) {
	t := b.topic(topicName)
	ch := make(chan Message, 64)
	t.mu.Lock()
	t.Subscribers[ch] = r.RemoteAddr
	t.mu.Unlock()
	log.Printf("Subscriber added topic=%s %s", topicName, r.RemoteAddr)

	w.Header().Set("Content-Type", "application/x-ndjson")
	w.WriteHeader(200)
	flusher, _ := w.(http.Flusher)
	if flusher != nil {
		// send the status and headers now, so the subscriber knows it is subscribed
		flusher.Flush()
	}
	enc := json.NewEncoder(w)
loop:
	for {
		select {
		case m, ok := <-ch:
			if !ok {
				break loop
			}
			if err := enc.Encode(m); err != nil {
				break loop
			}
			if flusher != nil {
				flusher.Flush()
			}
		case <-r.Context().Done():
			break loop
		}
	}
	t.mu.Lock()
	if _, ok := t.Subscribers[ch]; ok {
		delete(t.Subscribers, ch)
	}
	t.mu.Unlock()
	log.Printf("Subscriber gone topic=%s %s", topicName, r.RemoteAddr)
}

func writeJSON(w http.ResponseWriter, status int, v interface{}) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func main() {
	addr := flag.String("addr", ":8080", "listen address")
	dataDir := flag.String("data", "broker-data", "data directory")
	tokens := flag.String("tokens", "tokens.txt", "tokens file")
	flag.Parse()
	b := NewBroker(*dataDir, *tokens)
	log.Printf("Broker listening on %s", *addr)
	log.Fatal(http.ListenAndServe(*addr, b))
}
