package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"

	pb "nplabs/lab4grpc/pb"
)

func main() {
	args := os.Args[1:]
	if len(args) < 2 {
		fmt.Println("Usage: subscriber <brokerUrl> <topic>")
		return
	}
	broker := args[0]
	topic := args[1]
	conn, err := grpc.NewClient(broker, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fmt.Println("connect error:", err)
		return
	}
	defer conn.Close()
	client := pb.NewBrokerClient(conn)
	fmt.Printf("Subscribing to %s topic=%s\n", broker, topic)
	stream, err := client.Subscribe(context.Background(), &pb.SubscribeRequest{Topic: topic})
	if err != nil {
		fmt.Println("RPC error:", err)
		return
	}
	for {
		m, err := stream.Recv()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			fmt.Println("stream error:", err)
			break
		}
		fmt.Printf("[%s] %s: %s\n", m.Topic, m.Sender, m.Body)
	}
	fmt.Println("Stream ended")
}
