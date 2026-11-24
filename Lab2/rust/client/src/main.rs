use serde_json::{json, Value};
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::thread;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 { println!("Usage: client <host> <port> [client|provider]"); return; }
    let host = &args[1];
    let port = &args[2];
    let role = if args.len() >= 4 { args[3].as_str() } else { "client" };
    if role != "client" && role != "provider" { println!("role must be client or provider"); return; }

    let stream = match TcpStream::connect(format!("{}:{}", host, port)) {
        Ok(s) => s,
        Err(e) => { println!("connect error: {}", e); return; }
    };
    let write_stream = stream.try_clone().unwrap();
    let read_stream = stream.try_clone().unwrap();
    println!("Connected to {}:{} as {}", host, port, role);

    // identify
    let mut ws = write_stream;
    let hello = json!({"cmd":"hello","role":role});
    writeln!(ws, "{}", hello).unwrap();
    ws.flush().unwrap();

    // receiver thread
    let rt = thread::spawn(move || {
        let mut reader = BufReader::new(read_stream);
        let mut line = String::new();
        loop {
            line.clear();
            match reader.read_line(&mut line) {
                Ok(0) => { println!("[server gone]"); std::process::exit(0); }
                Ok(_) => {}
                Err(_) => { println!("[server gone]"); std::process::exit(0); }
            }
            let trimmed = line.trim_end_matches('\n').trim_end_matches('\r').trim().to_string();
            if trimmed.is_empty() { continue; }
            handle_msg(&trimmed);
        }
    });

    println!("Commands: menu | order <product>:<qty> ... | deliver <product>:<qty> ... (provider) | quit");
    let stdin = std::io::stdin();
    for line in stdin.lock().lines() {
        let line = match line { Ok(l) => l, Err(_) => break };
        let line = line.trim().to_string();
        if line.is_empty() { continue; }
        if line == "quit" || line == "/quit" {
            let _ = writeln!(ws, "{}", json!({"cmd":"quit"}));
            let _ = ws.flush();
            break;
        }
        if line == "menu" {
            let _ = writeln!(ws, "{}", json!({"cmd":"menu"}));
            let _ = ws.flush();
            continue;
        }
        if line.starts_with("order ") {
            if let Some(items) = parse_items(&line[6..]) {
                let _ = writeln!(ws, "{}", json!({"cmd":"order","items":items}));
                let _ = ws.flush();
            } else { println!("bad items"); }
            continue;
        }
        if line.starts_with("deliver ") {
            if let Some(items) = parse_items(&line[8..]) {
                let _ = writeln!(ws, "{}", json!({"cmd":"deliver","items":items}));
                let _ = ws.flush();
            } else { println!("bad items"); }
            continue;
        }
        println!("Unknown command");
    }
    let _ = rt.join();
}

fn parse_items(s: &str) -> Option<Vec<Value>> {
    let parts: Vec<&str> = s.split_whitespace().collect();
    let mut list = Vec::new();
    for p in parts {
        let kv: Vec<&str> = p.split(':').collect();
        if kv.len() != 2 { return None; }
        let q: i64 = kv[1].parse().ok()?;
        if q <= 0 { return None; }
        list.push(json!({"product":kv[0],"qty":q}));
    }
    if list.is_empty() { None } else { Some(list) }
}

fn handle_msg(line: &str) {
    if let Ok(v) = serde_json::from_str::<Value>(line) {
        let t = v.get("type").and_then(|x| x.as_str()).unwrap_or("");
        match t {
            "menu" => {
                if let Some(st) = v.get("stock") {
                    print!("Stock: ");
                    if let Some(obj) = st.as_object() {
                        for (k, val) in obj { print!("{}={} ", k, val); }
                    }
                    println!();
                }
            }
            "order_accepted" => println!("Order {} accepted, prep {} ms", v["id"], v["prep"]),
            "order_refused" => println!("Order refused: {} only {} left", v["product"], v["available"]),
            "order_ready" => println!("Order {} READY", v["id"]),
            "alert" => println!("ALERT: {} stock={}", v["product"], v["stock"]),
            "delivery_ok" => println!("Delivery accepted"),
            "error" => println!("ERROR: {}", v["msg"]),
            "ok" => {}
            _ => println!("MSG: {}", line),
        }
    } else {
        println!("MSG: {}", line);
    }
}
