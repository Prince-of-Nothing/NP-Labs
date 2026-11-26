package main

import (
	"context"
	"encoding/json"
	"flag"
	"log"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/types/known/timestamppb"

	pb "nplabs/lab4grpc/pb"
)

type MessageRec struct {
	ID     int64     `json:"id"`
	Topic  string    `json:"topic"`
	Sender string    `json:"sender"`
	Body   string    `json:"body"`
	Time   time.Time `json:"time"`
}

type Topic struct {
	Name     string
	NextID   int64
	Messages []MessageRec
	Subs     map[chan *pb.Message]bool
	mu       sync.Mutex
}

type Broker struct {
	pb.UnimplementedBrokerServer
	dataDir    string
	tokensFile string
	mu         sync.Mutex
	topics     map[string]*Topic
	deliver    chan MessageRec
}

func NewBroker(dataDir, tokensFile string) *Broker {
	b := &Broker{
		dataDir:    dataDir,
		tokensFile: tokensFile,
		topics:     map[string]*Topic{},
		deliver:    make(chan MessageRec, 1024),
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
		t := &Topic{Name: name, NextID: 1, Subs: map[chan *pb.Message]bool{}}
		path := filepath.Join(b.dataDir, name, "messages.json")
		if data, err := os.ReadFile(path); err == nil {
			var msgs []MessageRec
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
		t = &Topic{Name: name, NextID: 1, Subs: map[chan *pb.Message]bool{}}
		b.topics[name] = t
		os.MkdirAll(filepath.Join(b.dataDir, name), 0755)
	}
	return t
}

func (b *Broker) persist(t *Topic) {
	t.mu.Lock()
	snapshot := make([]MessageRec, len(t.Messages))
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
		_ = os.Remove(path)
		_ = os.Rename(tmp, path)
	}
}

func toProto(m MessageRec) *pb.Message {
	return &pb.Message{
		Id: m.ID, Topic: m.Topic, Sender: m.Sender, Body: m.Body,
		Time: timestamppb.New(m.Time),
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
		subs := make([]chan *pb.Message, 0, len(t.Subs))
		for ch := range t.Subs {
			subs = append(subs, ch)
		}
		t.mu.Unlock()
		pm := toProto(msg)
		for _, ch := range subs {
			select {
			case ch <- pm:
				log.Printf("Delivered id=%d topic=%s", msg.ID, msg.Topic)
			default:
				t.mu.Lock()
				if t.Subs[ch] {
					delete(t.Subs, ch)
					close(ch)
				}
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
			for _, topic := range strings.Split(parts[2], ",") {
				ti.Topics[topic] = true
			}
			return ti
		}
	}
	return nil
}

// authorize checks the authorization metadata when a tokens file exists and
// returns the token owner (empty when authentication is disabled).
func (b *Broker) authorize(ctx context.Context, topic string, reqToken string) (string, error) {
	if _, err := os.Stat(b.tokensFile); err != nil {
		return "", nil
	}
	tok := reqToken
	if md, ok := metadata.FromIncomingContext(ctx); ok {
		if vals := md.Get("authorization"); len(vals) > 0 {
			tok = vals[0]
		}
	}
	if tok == "" {
		return "", status.Error(codes.Unauthenticated, "missing token")
	}
	tok = strings.TrimSpace(tok)
	if strings.HasPrefix(strings.ToLower(tok), "bearer ") {
		tok = strings.TrimSpace(tok[7:])
	}
	ti := b.token(tok)
	if ti == nil {
		return "", status.Error(codes.Unauthenticated, "unknown token")
	}
	if !ti.Topics[topic] {
		return "", status.Error(codes.PermissionDenied, "topic not allowed")
	}
	return ti.Publisher, nil
}

func (b *Broker) Publish(ctx context.Context, req *pb.PublishRequest) (*pb.PublishReply, error) {
	owner, err := b.authorize(ctx, req.Topic, req.Token)
	if err != nil {
		log.Printf("Publish rejected topic=%s: %v", req.Topic, err)
		return nil, err
	}
	t := b.topic(req.Topic)
	t.mu.Lock()
	sender := req.Sender
	if owner != "" {
		sender = owner
	}
	msg := MessageRec{
		ID: t.NextID, Topic: req.Topic, Sender: sender, Body: req.Body, Time: time.Now().UTC(),
	}
	t.NextID++
	t.Messages = append(t.Messages, msg)
	t.mu.Unlock()

	b.persist(t)
	b.deliver <- msg
	log.Printf("Publish id=%d topic=%s", msg.ID, msg.Topic)
	return &pb.PublishReply{
		Id: msg.ID, Topic: msg.Topic, Sender: msg.Sender, Body: msg.Body,
		Time: timestamppb.New(msg.Time),
	}, nil
}

func (b *Broker) Subscribe(req *pb.SubscribeRequest, stream grpc.ServerStreamingServer[pb.Message]) error {
	t := b.topic(req.Topic)
	ch := make(chan *pb.Message, 64)
	t.mu.Lock()
	t.Subs[ch] = true
	t.mu.Unlock()
	log.Printf("Subscriber added topic=%s", req.Topic)
	defer func() {
		t.mu.Lock()
		if t.Subs[ch] {
			delete(t.Subs, ch)
		}
		t.mu.Unlock()
		log.Printf("Subscriber gone topic=%s", req.Topic)
	}()

	for {
		select {
		case m, ok := <-ch:
			if !ok {
				return nil
			}
			if err := stream.Send(m); err != nil {
				return err
			}
		case <-stream.Context().Done():
			return nil
		}
	}
}

func (b *Broker) ListTopics(ctx context.Context, req *pb.ListTopicsRequest) (*pb.ListTopicsReply, error) {
	reply := &pb.ListTopicsReply{}
	b.mu.Lock()
	for name, t := range b.topics {
		t.mu.Lock()
		reply.Topics = append(reply.Topics, &pb.TopicInfo{Name: name, Size: int32(len(t.Messages))})
		t.mu.Unlock()
	}
	b.mu.Unlock()
	return reply, nil
}

func main() {
	addr := flag.String("addr", ":5000", "listen address")
	dataDir := flag.String("data", "broker-data", "data directory")
	tokens := flag.String("tokens", "tokens.txt", "tokens file")
	flag.Parse()

	b := NewBroker(*dataDir, *tokens)
	lis, err := net.Listen("tcp", *addr)
	if err != nil {
		log.Fatalf("listen: %v", err)
	}
	srv := grpc.NewServer()
	pb.RegisterBrokerServer(srv, b)
	log.Printf("gRPC broker listening on %s", *addr)
	if err := srv.Serve(lis); err != nil {
		log.Fatalf("serve: %v", err)
	}
}
