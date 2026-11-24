use serde_json::{json, Value};
use std::collections::{HashMap, VecDeque};
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread;
use std::time::Duration;

const PRODUCTS: [&str; 3] = ["coffee", "tea", "croissant"];
fn prep_time(p: &str) -> i32 {
    match p { "coffee" => 4000, "tea" => 3000, "croissant" => 2000, _ => 0 }
}

struct Order {
    id: i32,
    client: Arc<Session>,
    items: HashMap<String, i32>,
    prep: i32,
    cancelled: AtomicBool,
}

struct Session {
    stream: Mutex<TcpStream>,
    endpoint: String,
    is_provider: Mutex<bool>,
    closed: AtomicBool,
    orders: Mutex<Vec<Arc<Order>>>,
}

struct State {
    stock: Mutex<HashMap<String, i32>>,
    sessions: Mutex<Vec<Arc<Session>>>,
    providers: Mutex<Vec<Arc<Session>>>,
    queue: Mutex<VecDeque<Arc<Order>>>,
    queue_cv: Condvar,
    next_id: AtomicI32,
}

impl State {
    fn new() -> Arc<Self> {
        let mut stock = HashMap::new();
        let mut rng_seed = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos() as i32;
        for p in PRODUCTS {
            // simple pseudo-random 10..=50
            rng_seed = rng_seed.wrapping_mul(1103515245).wrapping_add(12345);
            let v = (rng_seed.unsigned_abs() % 41) as i32 + 10;
            stock.insert(p.to_string(), v);
        }
        Arc::new(State {
            stock: Mutex::new(stock),
            sessions: Mutex::new(Vec::new()),
            providers: Mutex::new(Vec::new()),
            queue: Mutex::new(VecDeque::new()),
            queue_cv: Condvar::new(),
            next_id: AtomicI32::new(0),
        })
    }
}

fn log(msg: &str) {
    let now = chrono_like();
    println!("[{}] {}", now, msg);
}

fn chrono_like() -> String {
    // simple timestamp
    let d = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap();
    format!("{}", d.as_secs())
}

fn send_line(s: &Arc<Session>, v: &Value) {
    if s.closed.load(Ordering::SeqCst) { return; }
    let mut line = v.to_string();
    line.push('\n');
    let mut st = s.stream.lock().unwrap();
    let _ = st.write_all(line.as_bytes());
    let _ = st.flush();
}

fn stock_str(state: &State) -> String {
    let st = state.stock.lock().unwrap();
    PRODUCTS.iter().map(|p| format!("{}={}", p, st.get(*p).unwrap())).collect::<Vec<_>>().join(", ")
}

fn check_alerts(state: &State) {
    let low: Vec<String> = {
        let st = state.stock.lock().unwrap();
        PRODUCTS.iter().filter(|p| *st.get(**p).unwrap_or(&0) < 5).map(|p| p.to_string()).collect()
    };
    if low.is_empty() { return; }
    let providers: Vec<Arc<Session>> = state.providers.lock().unwrap().clone();
    if providers.is_empty() { return; }
    for p in &providers {
        if p.closed.load(Ordering::SeqCst) { continue; }
        for prod in &low {
            let stv = *state.stock.lock().unwrap().get(prod).unwrap_or(&0);
            send_line(p, &json!({"type":"alert","product":prod,"stock":stv}));
        }
    }
    log(&format!("Alert sent for {}", low.join(",")));
}

fn handle_disconnect(state: &State, s: &Arc<Session>) {
    if s.closed.swap(true, Ordering::SeqCst) { return; }
    // cancel orders
    let orders: Vec<Arc<Order>> = {
        let mut o = s.orders.lock().unwrap();
        let v = o.clone();
        o.clear();
        v
    };
    for o in orders {
        o.cancelled.store(true, Ordering::SeqCst);
    }
    // remove from sessions/providers
    {
        let mut ss = state.sessions.lock().unwrap();
        ss.retain(|x| !Arc::ptr_eq(x, s));
    }
    {
        let mut pp = state.providers.lock().unwrap();
        pp.retain(|x| !Arc::ptr_eq(x, s));
    }
    let _ = s.stream.lock().unwrap().shutdown(std::net::Shutdown::Both);
    log(&format!("Disconnected: {}", s.endpoint));
}

fn handle_client(state: Arc<State>, s: Arc<Session>) {
    let mut reader = {
        let st = s.stream.lock().unwrap().try_clone().unwrap();
        BufReader::new(st)
    };
    let mut line = String::new();
    let mut identified = false;
    loop {
        line.clear();
        let n = match reader.read_line(&mut line) {
            Ok(n) => n,
            Err(_) => 0,
        };
        if n == 0 {
            handle_disconnect(&state, &s);
            return;
        }
        if line.len() > 4096 {
            send_line(&s, &json!({"type":"error","msg":"message too long"}));
            continue;
        }
        let trimmed = line.trim_end_matches('\n').trim_end_matches('\r').trim().to_string();
        if trimmed.is_empty() { continue; }
        // parse
        let v: Value = match serde_json::from_str(&trimmed) {
            Ok(v) => v,
            Err(_) => {
                send_line(&s, &json!({"type":"error","msg":"invalid json"}));
                log(&format!("Invalid input from {}", s.endpoint));
                continue;
            }
        };
        let cmd = v.get("cmd").and_then(|x| x.as_str()).unwrap_or("").to_string();

        if !identified {
            if cmd != "hello" {
                send_line(&s, &json!({"type":"error","msg":"expected hello first"}));
                continue;
            }
            let role = v.get("role").and_then(|x| x.as_str()).unwrap_or("client");
            let is_prov = role == "provider";
            *s.is_provider.lock().unwrap() = is_prov;
            identified = true;
            if is_prov {
                state.providers.lock().unwrap().push(s.clone());
                log(&format!("Provider identified: {}", s.endpoint));
                // alerts
                let low: Vec<String> = {
                    let st = state.stock.lock().unwrap();
                    PRODUCTS.iter().filter(|p| *st.get(**p).unwrap_or(&0) < 5).map(|p| p.to_string()).collect()
                };
                for prod in low {
                    let stv = *state.stock.lock().unwrap().get(&prod).unwrap_or(&0);
                    send_line(&s, &json!({"type":"alert","product":prod,"stock":stv}));
                }
            }
            send_line(&s, &json!({"type":"ok","msg":"hello"}));
            continue;
        }

        match cmd.as_str() {
            "menu" => {
                let stock_copy: HashMap<String, i32> = state.stock.lock().unwrap().clone();
                send_line(&s, &json!({"type":"menu","stock":stock_copy}));
                log(&format!("MENU to {}", s.endpoint));
            }
            "order" => {
                // parse items
                let items_arr = match v.get("items").and_then(|x| x.as_array()) {
                    Some(a) => a,
                    None => { send_line(&s, &json!({"type":"error","msg":"missing items"})); continue; }
                };
                let mut items: HashMap<String, i32> = HashMap::new();
                let mut invalid = false;
                for it in items_arr {
                    let prod = it.get("product").and_then(|x| x.as_str()).unwrap_or("").to_string();
                    let qty = it.get("qty").and_then(|x| x.as_i64()).unwrap_or(0) as i32;
                    if !PRODUCTS.contains(&prod.as_str()) || qty <= 0 { invalid = true; break; }
                    *items.entry(prod).or_insert(0) += qty;
                }
                if invalid || items.is_empty() {
                    send_line(&s, &json!({"type":"error","msg":"invalid product or quantity"}));
                    log(&format!("Invalid order from {}", s.endpoint));
                    continue;
                }
                // check stock + decrement
                {
                    let mut st = state.stock.lock().unwrap();
                    for (p, q) in &items {
                        if *st.get(p).unwrap_or(&0) < *q {
                            let avail = *st.get(p).unwrap_or(&0);
                            send_line(&s, &json!({"type":"order_refused","product":p,"available":avail}));
                            log(&format!("Order refused for {}: {} need {} have {}", s.endpoint, p, q, avail));
                            // need skip; use flag
                            invalid = true;
                            break;
                        }
                    }
                    if !invalid {
                        for (p, q) in &items { *st.get_mut(p).unwrap() -= q; }
                    }
                }
                if invalid { continue; }

                let id = state.next_id.fetch_add(1, Ordering::SeqCst) + 1;
                let mut prep = 0;
                for (p, q) in &items { prep += prep_time(p) * q; }
                let order = Arc::new(Order {
                    id, client: s.clone(), items: items.clone(), prep,
                    cancelled: AtomicBool::new(false),
                });
                s.orders.lock().unwrap().push(order.clone());
                {
                    let mut q = state.queue.lock().unwrap();
                    q.push_back(order);
                }
                state.queue_cv.notify_one();
                send_line(&s, &json!({"type":"order_accepted","id":id,"prep":prep}));
                log(&format!("Order {} accepted from {}, prep {}ms", id, s.endpoint, prep));
                check_alerts(&state);
            }
            "deliver" => {
                let is_prov = *s.is_provider.lock().unwrap();
                if !is_prov {
                    send_line(&s, &json!({"type":"error","msg":"only provider can deliver"}));
                    log(&format!("Deliver refused (not provider) from {}", s.endpoint));
                    continue;
                }
                let items_arr = match v.get("items").and_then(|x| x.as_array()) {
                    Some(a) => a, None => { send_line(&s, &json!({"type":"error","msg":"missing items"})); continue; }
                };
                let mut items: HashMap<String, i32> = HashMap::new();
                let mut invalid = false;
                for it in items_arr {
                    let prod = it.get("product").and_then(|x| x.as_str()).unwrap_or("").to_string();
                    let qty = it.get("qty").and_then(|x| x.as_i64()).unwrap_or(0) as i32;
                    if !PRODUCTS.contains(&prod.as_str()) || qty <= 0 { invalid = true; break; }
                    *items.entry(prod).or_insert(0) += qty;
                }
                if invalid || items.is_empty() { send_line(&s, &json!({"type":"error","msg":"invalid product or quantity"})); continue; }
                {
                    let mut st = state.stock.lock().unwrap();
                    for (p, q) in &items { *st.get_mut(p).unwrap() += q; }
                }
                send_line(&s, &json!({"type":"delivery_ok","items":items}));
                log(&format!("Delivery from {}: {:?}", s.endpoint, items));
                check_alerts(&state);
            }
            "quit" => {
                send_line(&s, &json!({"type":"ok","msg":"bye"}));
                handle_disconnect(&state, &s);
                return;
            }
            _ => {
                send_line(&s, &json!({"type":"error","msg":"unknown command"}));
                log(&format!("Unknown command from {}: {}", s.endpoint, cmd));
            }
        }
    }
}

fn worker_loop(state: Arc<State>) {
    loop {
        let order = {
            let mut q = state.queue.lock().unwrap();
            while q.is_empty() {
                q = state.queue_cv.wait(q).unwrap();
            }
            q.pop_front().unwrap()
        };
        let mut cancelled = false;
        let mut remaining = order.prep;
        while remaining > 0 {
            if order.cancelled.load(Ordering::SeqCst) { cancelled = true; break; }
            let sleep = remaining.min(100);
            thread::sleep(Duration::from_millis(sleep as u64));
            remaining -= sleep;
        }
        if order.cancelled.load(Ordering::SeqCst) { cancelled = true; }

        if cancelled {
            {
                let mut st = state.stock.lock().unwrap();
                for (p, q) in &order.items { *st.get_mut(p).unwrap() += q; }
            }
            order.client.orders.lock().unwrap().retain(|o| o.id != order.id);
            log(&format!("Order {} cancelled (client gone); products returned", order.id));
            check_alerts(&state);
            continue;
        }
        let _ = send_line(&order.client, &json!({"type":"order_ready","id":order.id}));
        log(&format!("Order {} ready for {}", order.id, order.client.endpoint));
        order.client.orders.lock().unwrap().retain(|o| o.id != order.id);
    }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 { println!("Usage: server <port>"); return; }
    let port: u16 = args[1].parse().unwrap();
    let state = State::new();
    {
        let st = state.stock.lock().unwrap();
        println!("Stock initialized: {}", PRODUCTS.iter().map(|p| format!("{}={}", p, st.get(*p).unwrap())).collect::<Vec<_>>().join(", "));
    }
    let workers = (std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1)).max(1);
    for i in 0..workers {
        let st = state.clone();
        thread::Builder::new().name(format!("worker-{}", i)).spawn(move || worker_loop(st)).unwrap();
    }
    println!("Started {} workers", workers);
    let listener = TcpListener::bind(format!("0.0.0.0:{}", port)).unwrap();
    println!("Listening on 0.0.0.0:{}", port);
    for stream in listener.incoming() {
        match stream {
            Ok(stream) => {
                let peer = stream.peer_addr().map(|a| a.to_string()).unwrap_or("?".to_string());
                println!("[{}] Connection from {}", chrono_like(), peer);
                let s = Arc::new(Session {
                    stream: Mutex::new(stream),
                    endpoint: peer,
                    is_provider: Mutex::new(false),
                    closed: AtomicBool::new(false),
                    orders: Mutex::new(Vec::new()),
                });
                state.sessions.lock().unwrap().push(s.clone());
                let st = state.clone();
                thread::spawn(move || handle_client(st, s));
            }
            Err(e) => println!("Accept error: {}", e),
        }
    }
}
