package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"strings"
)

type Message struct {
	Topic  string `json:"topic"`
	Sender string `json:"sender"`
	Body   string `json:"body"`
}

func main() {
	args := os.Args[1:]
	if len(args) < 2 {
		fmt.Println("Usage: subscriber <brokerUrl> <topic>")
		return
	}
	base := strings.TrimSuffix(args[0], "/")
	topic := args[1]
	fmt.Printf("Subscribing to %s topic=%s\n", base, topic)
	resp, err := http.Get(base + "/topics/" + topic + "/stream")
	if err != nil {
		fmt.Println("error:", err)
		return
	}
	defer resp.Body.Close()
	scanner := bufio.NewScanner(resp.Body)
	scanner.Buffer(make([]byte, 4096), 1024*1024)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		var m Message
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			fmt.Println(line)
			continue
		}
		fmt.Printf("[%s] %s: %s\n", m.Topic, m.Sender, m.Body)
	}
	fmt.Println("Stream ended")
}
