package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"math/rand"
	"net"
	"os"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

var products = []string{"coffee", "tea", "croissant"}
var prepTime = map[string]int{"coffee": 4000, "tea": 3000, "croissant": 2000}

type Message map[string]interface{}

type Order struct {
	id     int
	client *Session
	items  map[string]int
	prep   int
	cancel chan struct{}
}

type Session struct {
	conn       net.Conn
	endpoint   string
	isProvider bool
	closed     atomic.Bool
	mu         sync.Mutex
	writeMu    sync.Mutex
	orders     []*Order
}

type State struct {
	stock       map[string]int
	stockMu     sync.Mutex
	stockFileMu sync.Mutex
	sessions    []*Session
	sessMu      sync.Mutex
	providers   []*Session
	provMu      sync.Mutex
	queue       chan *Order
	nextID      int
	idMu        sync.Mutex
}

// Bonus: stock survives a restart (atomic temp file + rename).
const stockStatePath = "stock-state.json"

func (s *State) saveStock() {
	s.stockFileMu.Lock()
	defer s.stockFileMu.Unlock()
	s.stockMu.Lock()
	snap := map[string]int{}
	for _, p := range products {
		snap[p] = s.stock[p]
	}
	s.stockMu.Unlock()
	b, err := json.MarshalIndent(snap, "", "  ")
	if err != nil {
		logMsg("Stock persist error: " + err.Error())
		return
	}
	tmp := stockStatePath + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		logMsg("Stock persist error: " + err.Error())
		return
	}
	if err := os.Rename(tmp, stockStatePath); err != nil {
		logMsg("Stock persist error: " + err.Error())
	}
}

func (s *State) loadStock() bool {
	b, err := os.ReadFile(stockStatePath)
	if err != nil {
		return false
	}
	var data map[string]int
	if err := json.Unmarshal(b, &data); err != nil {
		logMsg("Stock load failed (" + err.Error() + "); starting with a random stock")
		return false
	}
	clean := map[string]int{}
	for _, p := range products {
		v, ok := data[p]
		if !ok || v < 0 || v > 10_000_000 {
			return false
		}
		clean[p] = v
	}
	s.stockMu.Lock()
	for p, v := range clean {
		s.stock[p] = v
	}
	s.stockMu.Unlock()
	return true
}

func logMsg(msg string) {
	fmt.Printf("[%s] %s\n", time.Now().Format("15:04:05.000"), msg)
}

func (s *State) stockStr() string {
	s.stockMu.Lock()
	defer s.stockMu.Unlock()
	parts := []string{}
	for _, p := range products {
		parts = append(parts, fmt.Sprintf("%s=%d", p, s.stock[p]))
	}
	return strings.Join(parts, ", ")
}

func sendJSON(sess *Session, v interface{}) {
	b, _ := json.Marshal(v)
	b = append(b, '\n')
	sess.writeMu.Lock()
	defer sess.writeMu.Unlock()
	if sess.closed.Load() {
		return
	}
	sess.conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
	_, err := sess.conn.Write(b)
	if err != nil {
		sess.closed.Store(true)
	}
}

func (st *State) checkAlerts() {
	st.stockMu.Lock()
	low := []string{}
	for _, p := range products {
		if st.stock[p] < 5 {
			low = append(low, p)
		}
	}
	st.stockMu.Unlock()
	if len(low) == 0 {
		return
	}
	st.provMu.Lock()
	provs := append([]*Session(nil), st.providers...)
	st.provMu.Unlock()
	for _, p := range provs {
		if p.closed.Load() {
			continue
		}
		for _, prod := range low {
			st.stockMu.Lock()
			sv := st.stock[prod]
			st.stockMu.Unlock()
			sendJSON(p, map[string]interface{}{"type": "alert", "product": prod, "stock": sv})
		}
	}
	if len(provs) > 0 {
		logMsg(fmt.Sprintf("Alert sent for %s", strings.Join(low, ",")))
	}
}

func handleDisconnect(st *State, sess *Session) {
	sess.mu.Lock()
	if sess.closed.Load() {
		sess.mu.Unlock()
		return
	}
	sess.closed.Store(true)
	orders := append([]*Order(nil), sess.orders...)
	sess.orders = nil
	sess.mu.Unlock()

	for _, o := range orders {
		o.cancel <- struct{}{}
	}

	st.sessMu.Lock()
	filtered := []*Session{}
	for _, s := range st.sessions {
		if s != sess {
			filtered = append(filtered, s)
		}
	}
	st.sessions = filtered
	st.sessMu.Unlock()

	st.provMu.Lock()
	filteredP := []*Session{}
	for _, s := range st.providers {
		if s != sess {
			filteredP = append(filteredP, s)
		}
	}
	st.providers = filteredP
	st.provMu.Unlock()

	sess.conn.Close()
	logMsg(fmt.Sprintf("Disconnected: %s", sess.endpoint))
}

func workerLoop(st *State) {
	for order := range st.queue {
		cancelled := false
		remaining := order.prep
		for remaining > 0 {
			select {
			case <-order.cancel:
				cancelled = true
				remaining = 0
			default:
			}
			if cancelled {
				break
			}
			sleep := 100
			if remaining < sleep {
				sleep = remaining
			}
			time.Sleep(time.Duration(sleep) * time.Millisecond)
			remaining -= sleep
		}
		select {
		case <-order.cancel:
			cancelled = true
		default:
		}

		if cancelled {
			st.stockMu.Lock()
			for p, q := range order.items {
				st.stock[p] += q
			}
			st.stockMu.Unlock()
			st.saveStock()
			order.client.mu.Lock()
			filtered := []*Order{}
			for _, o := range order.client.orders {
				if o != order {
					filtered = append(filtered, o)
				}
			}
			order.client.orders = filtered
			order.client.mu.Unlock()
			logMsg(fmt.Sprintf("Order %d cancelled (client gone); products returned", order.id))
			st.checkAlerts()
			continue
		}
		sendJSON(order.client, map[string]interface{}{"type": "order_ready", "id": order.id})
		logMsg(fmt.Sprintf("Order %d ready for %s", order.id, order.client.endpoint))
		order.client.mu.Lock()
		filtered := []*Order{}
		for _, o := range order.client.orders {
			if o != order {
				filtered = append(filtered, o)
			}
		}
		order.client.orders = filtered
		order.client.mu.Unlock()
	}
}

func handleClient(st *State, sess *Session) {
	defer handleDisconnect(st, sess)
	reader := bufio.NewReader(sess.conn)
	identified := false
	for {
		line, err := reader.ReadString('\n')
		if err != nil {
			// maybe last line
			if line == "" {
				return
			}
		}
		if len(line) > 4096 {
			sendJSON(sess, map[string]interface{}{"type": "error", "msg": "message too long"})
			continue
		}
		line = strings.TrimRight(line, "\r\n")
		if strings.TrimSpace(line) == "" {
			if err != nil {
				return
			}
			continue
		}
		var v map[string]interface{}
		if json.Unmarshal([]byte(line), &v) != nil {
			sendJSON(sess, map[string]interface{}{"type": "error", "msg": "invalid json"})
			logMsg(fmt.Sprintf("Invalid input from %s", sess.endpoint))
			if err != nil {
				return
			}
			continue
		}
		cmd, _ := v["cmd"].(string)
		if !identified {
			if cmd != "hello" {
				sendJSON(sess, map[string]interface{}{"type": "error", "msg": "expected hello first"})
				if err != nil {
					return
				}
				continue
			}
			role, _ := v["role"].(string)
			if role == "provider" {
				sess.isProvider = true
				st.provMu.Lock()
				st.providers = append(st.providers, sess)
				st.provMu.Unlock()
				logMsg(fmt.Sprintf("Provider identified: %s", sess.endpoint))
				// alerts
				st.stockMu.Lock()
				for _, p := range products {
					if st.stock[p] < 5 {
						sendJSON(sess, map[string]interface{}{"type": "alert", "product": p, "stock": st.stock[p]})
					}
				}
				st.stockMu.Unlock()
			}
			identified = true
			sendJSON(sess, map[string]interface{}{"type": "ok", "msg": "hello"})
			if err != nil {
				return
			}
			continue
		}
		switch cmd {
		case "menu":
			st.stockMu.Lock()
			cp := map[string]int{}
			for _, p := range products {
				cp[p] = st.stock[p]
			}
			st.stockMu.Unlock()
			sendJSON(sess, map[string]interface{}{"type": "menu", "stock": cp})
			logMsg(fmt.Sprintf("MENU to %s", sess.endpoint))
		case "order":
			itemsArr, _ := v["items"].([]interface{})
			items := map[string]int{}
			invalid := false
			for _, it := range itemsArr {
				m, _ := it.(map[string]interface{})
				prod, _ := m["product"].(string)
				qf, _ := m["qty"].(float64)
				q := int(qf)
				okProd := false
				for _, p := range products {
					if p == prod {
						okProd = true
					}
				}
				if !okProd || q <= 0 {
					invalid = true
					break
				}
				items[prod] += q
			}
			if invalid || len(items) == 0 {
				sendJSON(sess, map[string]interface{}{"type": "error", "msg": "invalid product or quantity"})
				logMsg(fmt.Sprintf("Invalid order from %s", sess.endpoint))
				continue
			}
			// check stock
			st.stockMu.Lock()
			insufficient := false
			var badProd string
			var avail int
			for p, q := range items {
				if st.stock[p] < q {
					insufficient = true
					badProd = p
					avail = st.stock[p]
					break
				}
			}
			if insufficient {
				st.stockMu.Unlock()
				sendJSON(sess, map[string]interface{}{"type": "order_refused", "product": badProd, "available": avail})
				logMsg(fmt.Sprintf("Order refused for %s: %s", sess.endpoint, badProd))
				continue
			}
			for p, q := range items {
				st.stock[p] -= q
			}
			st.stockMu.Unlock()
			st.saveStock()

			st.idMu.Lock()
			st.nextID++
			id := st.nextID
			st.idMu.Unlock()

			prep := 0
			for p, q := range items {
				prep += prepTime[p] * q
			}
			order := &Order{id: id, client: sess, items: items, prep: prep, cancel: make(chan struct{}, 1)}
			sess.mu.Lock()
			sess.orders = append(sess.orders, order)
			sess.mu.Unlock()

			st.queue <- order
			sendJSON(sess, map[string]interface{}{"type": "order_accepted", "id": id, "prep": prep})
			logMsg(fmt.Sprintf("Order %d accepted from %s, prep %dms", id, sess.endpoint, prep))
			st.checkAlerts()
		case "deliver":
			if !sess.isProvider {
				sendJSON(sess, map[string]interface{}{"type": "error", "msg": "only provider can deliver"})
				logMsg(fmt.Sprintf("Deliver refused (not provider) from %s", sess.endpoint))
				continue
			}
			itemsArr, _ := v["items"].([]interface{})
			items := map[string]int{}
			invalid := false
			for _, it := range itemsArr {
				m, _ := it.(map[string]interface{})
				prod, _ := m["product"].(string)
				qf, _ := m["qty"].(float64)
				q := int(qf)
				okProd := false
				for _, p := range products {
					if p == prod {
						okProd = true
					}
				}
				if !okProd || q <= 0 {
					invalid = true
					break
				}
				items[prod] += q
			}
			if invalid || len(items) == 0 {
				sendJSON(sess, map[string]interface{}{"type": "error", "msg": "invalid product or quantity"})
				continue
			}
			st.stockMu.Lock()
			for p, q := range items {
				st.stock[p] += q
			}
			st.stockMu.Unlock()
			st.saveStock()
			sendJSON(sess, map[string]interface{}{"type": "delivery_ok", "items": items})
			logMsg(fmt.Sprintf("Delivery from %s", sess.endpoint))
			st.checkAlerts()
		case "quit":
			sendJSON(sess, map[string]interface{}{"type": "ok", "msg": "bye"})
			handleDisconnect(st, sess)
			return
		default:
			sendJSON(sess, map[string]interface{}{"type": "error", "msg": "unknown command"})
			logMsg(fmt.Sprintf("Unknown command from %s: %s", sess.endpoint, cmd))
		}
		if err != nil {
			return
		}
	}
}

func main() {
	args := os.Args[1:]
	if len(args) < 1 {
		fmt.Println("Usage: server <port>")
		return
	}
	port := args[0]
	st := &State{
		stock: map[string]int{},
		queue: make(chan *Order, 1000),
	}
	if st.loadStock() {
		logMsg(fmt.Sprintf("Stock loaded from %s: %s", stockStatePath, st.stockStr()))
	} else {
		rng := rand.New(rand.NewSource(time.Now().UnixNano()))
		for _, p := range products {
			st.stock[p] = rng.Intn(41) + 10
		}
		st.saveStock()
		logMsg(fmt.Sprintf("Stock initialized: %s", st.stockStr()))
	}
	workers := runtime.NumCPU()
	if workers < 1 {
		workers = 1
	}
	for i := 0; i < workers; i++ {
		go workerLoop(st)
	}
	logMsg(fmt.Sprintf("Started %d workers", workers))

	ln, err := net.Listen("tcp", "0.0.0.0:"+port)
	if err != nil {
		fmt.Println(err)
		return
	}
	logMsg(fmt.Sprintf("Listening on 0.0.0.0:%s", port))
	for {
		conn, err := ln.Accept()
		if err != nil {
			logMsg(fmt.Sprintf("Accept error: %s", err))
			continue
		}
		ep := conn.RemoteAddr().String()
		logMsg(fmt.Sprintf("Connection from %s", ep))
		sess := &Session{conn: conn, endpoint: ep}
		st.sessMu.Lock()
		st.sessions = append(st.sessions, sess)
		st.sessMu.Unlock()
		go handleClient(st, sess)
	}
}
