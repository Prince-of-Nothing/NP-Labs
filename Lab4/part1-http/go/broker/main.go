package main

import (
	"encoding/json"
	"flag"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
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

type Topic struct {
	Name        string
	NextID      int64
	Messages    []Message
	Subscribers map[chan Message]bool
	mu          sync.Mutex
}

type Broker struct {
	dataDir    string
	tokensFile string
	mu         sync.Mutex
	topics     map[string]*Topic
	deliver    chan Message
}

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
		t := &Topic{Name: name, Subscribers: map[chan Message]bool{}, NextID: 1}
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
		t = &Topic{Name: name, Subscribers: map[chan Message]bool{}, NextID: 1}
		b.topics[name] = t
		os.MkdirAll(filepath.Join(b.dataDir, name), 0755)
	}
	return t
}

func (b *Broker) persist(t *Topic) {
	t.mu.Lock()
	snapshot := make([]Message, len(t.Messages))
	copy(snapshot, t.Messages)
	t.mu.Unlock()
	data, err := json.Marshal(snapshot)
	if err != nil {
		return
	}
	path := filepath.Join(b.dataDir, t.Name, "messages.json")
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0644); err != nil {
		return
	}
	if err := os.Rename(tmp, path); err != nil {
		// Windows cannot rename over an existing file.
		_ = os.Remove(path)
		_ = os.Rename(tmp, path)
	}
}

func (b *Broker) deliveryLoop() {
	for msg := range b.deliver {
		b.mu.Lock()
		t := b.topics[msg.Topic]
		b.mu.Unlock()
		if t == nil {
			continue
		}
		t.mu.Lock()
		subs := make([]chan Message, 0, len(t.Subscribers))
		for ch := range t.Subscribers {
			subs = append(subs, ch)
		}
		t.mu.Unlock()
		for _, ch := range subs {
			select {
			case ch <- msg:
				log.Printf("Delivered id=%d topic=%s", msg.ID, msg.Topic)
			default:
				// slow or gone subscriber: drop it
				t.mu.Lock()
				delete(t.Subscribers, ch)
				close(ch)
				t.mu.Unlock()
				log.Printf("Subscriber removed (slow) topic=%s", msg.Topic)
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

	if r.Method == "POST" && strings.HasPrefix(path, "/topics/") && strings.HasSuffix(path, "/messages") {
		name := strings.TrimSuffix(strings.TrimPrefix(path, "/topics/"), "/messages")
		b.publish(w, r, name)
		return
	}

	if r.Method == "GET" && strings.HasPrefix(path, "/topics/") && strings.HasSuffix(path, "/stream") {
		name := strings.TrimSuffix(strings.TrimPrefix(path, "/topics/"), "/stream")
		b.stream(w, r, name)
		return
	}

	log.Printf("404 %s %s from %s", r.Method, path, r.RemoteAddr)
	http.NotFound(w, r)
}

func (b *Broker) publish(w http.ResponseWriter, r *http.Request, topicName string) {
	if _, err := os.Stat(b.tokensFile); err == nil {
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
			log.Printf("Publish unauthorized (unknown token) %s topic=%s", r.RemoteAddr, topicName)
			return
		}
		if !ti.Topics[topicName] {
			w.WriteHeader(403)
			log.Printf("Publish forbidden %s topic=%s", r.RemoteAddr, topicName)
			return
		}
	}

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
	t.mu.Lock()
	msg := Message{ID: t.NextID, Topic: topicName, Sender: in.Sender, Body: in.Body, Time: time.Now().UTC()}
	t.NextID++
	t.Messages = append(t.Messages, msg)
	t.mu.Unlock()

	b.persist(t)
	b.deliver <- msg
	writeJSON(w, 202, msg)
	log.Printf("Publish accepted id=%d topic=%s from %s", msg.ID, topicName, r.RemoteAddr)
}

func (b *Broker) stream(w http.ResponseWriter, r *http.Request, topicName string) {
	t := b.topic(topicName)
	ch := make(chan Message, 64)
	t.mu.Lock()
	t.Subscribers[ch] = true
	t.mu.Unlock()
	log.Printf("Subscriber added topic=%s %s", topicName, r.RemoteAddr)

	w.Header().Set("Content-Type", "application/x-ndjson")
	w.WriteHeader(200)
	flusher, _ := w.(http.Flusher)
	enc := json.NewEncoder(w)
	for {
		select {
		case m, ok := <-ch:
			if !ok {
				goto done
			}
			if err := enc.Encode(m); err != nil {
				goto done
			}
			if flusher != nil {
				flusher.Flush()
			}
		case <-r.Context().Done():
			goto done
		}
	}
done:
	t.mu.Lock()
	if t.Subscribers[ch] {
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
