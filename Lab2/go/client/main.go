package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"net"
	"os"
	"strings"
)

func main() {
	args := os.Args[1:]
	if len(args) < 2 {
		fmt.Println("Usage: client <host> <port> [client|provider]")
		return
	}
	host := args[0]
	port := args[1]
	role := "client"
	if len(args) >= 3 {
		role = args[2]
	}
	conn, err := net.Dial("tcp", host+":"+port)
	if err != nil {
		fmt.Println("connect error:", err)
		return
	}
	defer conn.Close()
	fmt.Printf("Connected to %s:%s as %s\n", host, port, role)

	// identify
	enc := json.NewEncoder(conn)
	enc.Encode(map[string]interface{}{"cmd": "hello", "role": role})

	// receiver goroutine
	go func() {
		dec := json.NewDecoder(conn)
		for {
			var v map[string]interface{}
			if err := dec.Decode(&v); err != nil {
				fmt.Println("[server gone]")
				os.Exit(0)
			}
			t, _ := v["type"].(string)
			switch t {
			case "menu":
				if st, ok := v["stock"].(map[string]interface{}); ok {
					fmt.Print("Stock: ")
					for k, val := range st {
						fmt.Printf("%s=%v ", k, val)
					}
					fmt.Println()
				}
			case "order_accepted":
				fmt.Printf("Order %.0f accepted, prep %.0f ms\n", v["id"], v["prep"])
			case "order_refused":
				fmt.Printf("Order refused: %v only %.0f left\n", v["product"], v["available"])
			case "order_ready":
				fmt.Printf("Order %.0f READY\n", v["id"])
			case "alert":
				fmt.Printf("ALERT: %v stock=%.0f\n", v["product"], v["stock"])
			case "delivery_ok":
				fmt.Println("Delivery accepted")
			case "error":
				fmt.Printf("ERROR: %v\n", v["msg"])
			}
		}
	}()

	fmt.Println("Commands: menu | order <product>:<qty> ... | deliver <product>:<qty> ... (provider) | quit")
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		if line == "quit" || line == "/quit" {
			enc.Encode(map[string]interface{}{"cmd": "quit"})
			return
		}
		if line == "menu" {
			enc.Encode(map[string]interface{}{"cmd": "menu"})
			continue
		}
		if strings.HasPrefix(line, "order ") {
			items := parseItems(line[6:])
			if items == nil {
				fmt.Println("bad items")
				continue
			}
			enc.Encode(map[string]interface{}{"cmd": "order", "items": items})
			continue
		}
		if strings.HasPrefix(line, "deliver ") {
			items := parseItems(line[8:])
			if items == nil {
				fmt.Println("bad items")
				continue
			}
			enc.Encode(map[string]interface{}{"cmd": "deliver", "items": items})
			continue
		}
		fmt.Println("Unknown command")
	}
}

func parseItems(s string) []map[string]interface{} {
	parts := strings.Fields(s)
	var list []map[string]interface{}
	for _, p := range parts {
		kv := strings.Split(p, ":")
		if len(kv) != 2 {
			return nil
		}
		q, err := parseInt(kv[1])
		if err != nil || q <= 0 {
			return nil
		}
		list = append(list, map[string]interface{}{"product": kv[0], "qty": q})
	}
	if len(list) == 0 {
		return nil
	}
	return list
}

func parseInt(s string) (int, error) {
	// simple
	n := 0
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0, fmt.Errorf("bad")
		}
		n = n*10 + int(c-'0')
	}
	return n, nil
}
