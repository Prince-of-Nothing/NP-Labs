package main

import (
	"bufio"
	"context"
	"fmt"
	"os"
	"strings"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"

	pb "nplabs/lab4grpc/pb"
)

func main() {
	args := os.Args[1:]
	if len(args) < 3 {
		fmt.Println("Usage: publisher <brokerUrl> <topic> <sender> [token]")
		return
	}
	broker := args[0]
	topic := args[1]
	sender := args[2]
	token := ""
	if len(args) >= 4 {
		token = args[3]
	}
	conn, err := grpc.NewClient(broker, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		fmt.Println("connect error:", err)
		return
	}
	defer conn.Close()
	client := pb.NewBrokerClient(conn)

	fmt.Printf("Publishing to %s topic=%s as %s. Type lines:\n", broker, topic, sender)
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		line := scanner.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}
		ctx := context.Background()
		req := &pb.PublishRequest{Topic: topic, Sender: sender, Body: line, Token: token}
		if token != "" {
			ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
		}
		reply, err := client.Publish(ctx, req)
		if err != nil {
			fmt.Println("RPC error:", err)
			continue
		}
		fmt.Printf("Published id=%d\n", reply.Id)
	}
}
