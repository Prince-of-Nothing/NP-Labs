package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
)

func main() {
	args := os.Args[1:]
	if len(args) < 3 {
		fmt.Println("Usage: publisher <brokerUrl> <topic> <sender> [token]")
		return
	}
	base := strings.TrimSuffix(args[0], "/")
	topic := args[1]
	sender := args[2]
	token := ""
	if len(args) >= 4 {
		token = args[3]
	}
	fmt.Printf("Publishing to %s topic=%s as %s. Type lines:\n", base, topic, sender)
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		line := scanner.Text()
		if strings.TrimSpace(line) == "" {
			continue
		}
		payload, _ := json.Marshal(map[string]string{"sender": sender, "body": line})
		req, _ := http.NewRequest("POST", base+"/topics/"+topic+"/messages", bytes.NewReader(payload))
		req.Header.Set("Content-Type", "application/json")
		if token != "" {
			req.Header.Set("Authorization", "Bearer "+token)
		}
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			fmt.Println("error:", err)
			continue
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		fmt.Printf("%d %s: %s", resp.StatusCode, resp.Status, string(body))
	}
}
