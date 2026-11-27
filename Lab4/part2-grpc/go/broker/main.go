package main

import (
	"context"
	"encoding/json"
	"flag"
	"log"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/peer"
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

// Topic holds the messages of one topic. mu protects its fields. pubMu makes one
// publish at a time: take the id, store the message, write the file and queue the
// delivery all happen under it, so the ids, the file and the delivery order agree.
type Topic struct {
	Name     string
	NextID   int64
	Messages []MessageRec
	Subs     map[chan *pb.Message]string // open streams -> client address
	mu       sync.Mutex
	pubMu    sync.Mutex
}

type Broker struct {
	pb.UnimplementedBrokerServer
	dataDir    string
	tokensFile string
	mu         sync.Mutex // protects topics
	topics     map[string]*Topic

	// Published messages waiting for delivery, in publish order. A publish waits
	// here only when the deliveries are far behind (backpressure).
	deliver chan MessageRec
}

// validTopic: letters, digits, '-' and '_', 1 to 64 characters. A topic name is
// used as a directory name, so nothing else is allowed.
var validTopic = regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

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
		t := &Topic{Name: name, NextID: 1, Subs: map[chan *pb.Message]string{}}
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
		t = &Topic{Name: name, NextID: 1, Subs: map[chan *pb.Message]string{}}
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

func toProto(m MessageRec) *pb.Message {
	return &pb.Message{
		Id: m.ID, Topic: m.Topic, Sender: m.Sender, Body: m.Body,
		Time: timestamppb.New(m.Time),
	}
}

// deliveryLoop writes the published messages, one at a time and in publish order,
// to the open streams of each topic. Every subscriber therefore sees one topic's
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
		subs := make(map[chan *pb.Message]string, len(t.Subs))
		for ch, client := range t.Subs {
			subs[ch] = client
		}
		t.mu.Unlock()
		pm := toProto(msg)
		for ch, client := range subs {
			select {
			case ch <- pm:
				log.Printf("Delivered id=%d topic=%s to %s", msg.ID, msg.Topic, client)
			default:
				// the subscriber stopped reading: forget it; the others still get the message
				t.mu.Lock()
				if _, ok := t.Subs[ch]; ok {
					delete(t.Subs, ch)
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
			for _, topic := range strings.Split(parts[2], ",") {
				ti.Topics[topic] = true
			}
			return ti
		}
	}
	return nil
}

// peerAddr returns the caller's address for the logs.
func peerAddr(ctx context.Context) string {
	if p, ok := peer.FromContext(ctx); ok {
		return p.Addr.String()
	}
	return "unknown"
}

// authorize requires a token from the tokens file on every publish and returns
// the token's publisher. With no tokens file no token can match, so every
// publish is refused (fail closed).
func (b *Broker) authorize(ctx context.Context, topic string, reqToken string) (string, error) {
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
	client := peerAddr(ctx)
	owner, err := b.authorize(ctx, req.Topic, req.Token)
	if err != nil {
		log.Printf("Publish rejected topic=%s from %s: %v", req.Topic, client, err)
		return nil, err
	}
	if !validTopic.MatchString(req.Topic) {
		log.Printf("Publish rejected: invalid topic name %q from %s", req.Topic, client)
		return nil, status.Error(codes.InvalidArgument, "invalid topic name")
	}

	t := b.topic(req.Topic)
	t.pubMu.Lock() // one publish at a time per topic: ids, the file and the delivery order agree
	t.mu.Lock()
	// The sender is the token's publisher; the sender field of the request is ignored.
	msg := MessageRec{
		ID: t.NextID, Topic: req.Topic, Sender: owner, Body: req.Body, Time: time.Now().UTC(),
	}
	t.Messages = append(t.Messages, msg)
	if err := b.persist(t); err != nil {
		// not saved, so not accepted: take the message back out
		t.Messages = t.Messages[:len(t.Messages)-1]
		t.mu.Unlock()
		t.pubMu.Unlock()
		log.Printf("Publish failed (not saved) topic=%s from %s: %v", req.Topic, client, err)
		return nil, status.Error(codes.Internal, "could not save the message")
	}
	t.NextID++
	t.mu.Unlock()
	b.deliver <- msg // waits here only when the deliveries are far behind
	t.pubMu.Unlock()

	log.Printf("Publish id=%d topic=%s from %s", msg.ID, msg.Topic, client)
	return &pb.PublishReply{
		Id: msg.ID, Topic: msg.Topic, Sender: msg.Sender, Body: msg.Body,
		Time: timestamppb.New(msg.Time),
	}, nil
}

func (b *Broker) Subscribe(req *pb.SubscribeRequest, stream grpc.ServerStreamingServer[pb.Message]) error {
	if !validTopic.MatchString(req.Topic) {
		return status.Error(codes.InvalidArgument, "invalid topic name")
	}
	client := peerAddr(stream.Context())
	t := b.topic(req.Topic)
	ch := make(chan *pb.Message, 64)
	t.mu.Lock()
	t.Subs[ch] = client
	t.mu.Unlock()
	log.Printf("Subscriber added topic=%s %s", req.Topic, client)
	defer func() {
		t.mu.Lock()
		if _, ok := t.Subs[ch]; ok {
			delete(t.Subs, ch)
		}
		t.mu.Unlock()
		log.Printf("Subscriber gone topic=%s %s", req.Topic, client)
	}()

	// send the headers now, so the subscriber knows the stream is open before the first message
	if err := stream.SendHeader(metadata.MD{}); err != nil {
		return err
	}

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
