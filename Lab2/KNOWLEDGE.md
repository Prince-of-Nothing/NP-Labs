# Lab 2 — Coffee Shop: implementation notes and defense answers

## What is implemented

- Raw TCP sockets only: C# `Socket`, Rust `std::net`, Go `net`.
- Server + one client program that runs as `client` or `provider`.
- Stock: random 10–50 per product; coffee 4 s, tea 3 s, croissant 2 s per unit.
- One worker per logical CPU (`Environment.ProcessorCount`, `available_parallelism`, `runtime.NumCPU`).
- FIFO order queue; a free worker takes the oldest order; one whole order per worker.
- Stock check + decrement under one lock, so the last item cannot be sold twice.
- Low-stock (<5) alert to the provider immediately, and on provider reconnect.
- Client disconnect cancels queued and in-progress orders; products return to stock.
- Newline-delimited JSON protocol; max line 4096 bytes; invalid input gets an error.
- Per-session send lock, so worker pushes (`order_ready`) and handler replies cannot interleave.

## Verified

- 20 clients against 12 workers: 20 accepted, 20 ready.
- Last croissant race: 1 accepted, 1 refused.
- Disconnect mid-order: stock returns (coffee 43 → 45).
- Provider disconnect: shop keeps serving.
- Server death: C#, Rust, Go clients exit cleanly.

## Task 1

**1. How are bytes cut into messages? What if a message is split or two arrive together?**
The protocol is one JSON object per line (`\n`). Each connection keeps a string
buffer. After every `Receive`, the bytes are appended and the buffer is split on
`\n`; each complete line is handled and the remainder stays in the buffer. So a
message split over two receives is completed on the next receive, and two
messages in one receive are processed by the `while` loop one after the other.
A line longer than 4096 bytes is an error.

**2. Why answer at once and tell "ready" later? What would the client see otherwise?**
Accepting the order is fast: check and decrement stock, assign an id, enqueue.
Preparation takes seconds and runs on a worker. Answering at once gives the
client its id and preparation time and lets it keep using the connection
(menu, another order). Otherwise the client would block for the whole
preparation and see nothing until the order was done.

## Task 2

**1. Which data is shared and how is it protected?**
- `Stock`: `StockLock` (C# `lock`, Rust `Mutex`, Go `sync.Mutex`).
- Order queue: `BlockingCollection` / `VecDeque + Condvar` / Go channel — thread-safe.
- Next order id: `Interlocked` / `AtomicI32` / mutex-protected counter.
- Sessions and provider list: `SessionsLock`.
- Per-client order list: `ClientSession.Lock` / session mutex.
- Socket writes: per-session send lock (`WriteLock`, Rust stream mutex, Go `writeMu`).
- Console/log output: `ConsoleLock` (C#) or log mutex.

**2. Last croissant race, step by step without protection.**
1. Stock = 1. Thread A reads 1, decides it can sell.
2. Before A writes, thread B reads 1 and also decides it can sell.
3. A writes 0, accepts order A.
4. B writes 0 (or −1), accepts order B.
Both clients get the croissant, and stock is wrong. With one lock around
"check every item and decrement", step 1–4 is atomic: the second thread sees 0
and gets `order_refused` with the remaining amount.

**3. A worker sends "ready" while the client thread reads the same connection. What must be handled?**
Two threads writing to one TCP socket at the same time can interleave bytes and
corrupt the JSON lines. Every send must go through one per-session lock. In this
project C# uses `ClientSession.WriteLock`, Rust locks the stream `Mutex`, Go
uses `Session.writeMu`.

**4. Why one worker per CPU core? What with twice as many?**
Preparation is simulated by sleeping, so workers mostly wait, not compute. One
per core is the required default and avoids idle CPU-bound threads. With twice
as many workers, more orders could be "preparing" at once because sleeping is
not CPU work, but it adds threads, stacks and context switching; the benefit is
only concurrency of orders, not CPU throughput.

## Task 3

**1. How does the server know which connection is the provider?**
The first message must be `hello` with `role:"provider"`. The server marks that
session and adds it to the provider list; only those sessions may `deliver`.

**2. Delivery and order change stock at the same moment — show no change is lost.**
Both operations take the same `StockLock`. A delivery adds inside the lock, an
order checks and subtracts inside the lock. They serialize: one completes
before the other starts, so the final value is `old + delivery − order`, never a
stale overwrite.

**3. Which thread sends the alert, and when?**
The thread that changed the stock (the client handler for an order, or the
provider handler for a delivery) calls `CheckAlerts` after releasing the stock
lock and sends the alert to every connected provider. On provider connect,
`SendAlerts` sends the current low-stock products.

## Task 4

**1. How is a disappeared client noticed? At once or not?**
- Closed socket / killed process: `Receive` returns 0 or throws (FIN/RST) → noticed at once.
- Network cable/Wi-Fi gone: no packet arrives and no FIN; the server only notices
  on a later send failure or after a TCP timeout. A heartbeat would be needed for
  a fast detection; this lab detects it on read/send failure.

**2. Client disappears while a worker prepares its order. How does the worker find out and stop? How do products return?**
Each order has a `CancellationTokenSource` (Rust `AtomicBool`, Go `cancel` channel).
Disconnect cancels it. The worker sleeps in 100 ms chunks and checks the token;
when cancelled it stops, returns all order items to stock under `StockLock`, and
removes the order. Time already spent is lost; the next order starts its full
preparation again.

**3. Why must the client receive and read the keyboard at the same time?**
`ReadLine` blocks until the user types, so a single-threaded client could not
show `order_ready` or `alert` while waiting for input. The client uses one
receiver thread/goroutine that prints server messages, while the main thread
reads stdin; console output is serialized so lines do not mix.

## Control questions

1. **Listening socket vs accepted sockets.** One listening socket is bound and
   calls `listen`; every `accept` returns a new connected socket. Five clients +
   provider = 1 listening + 6 connected = 7 sockets.
2. **Why one send is not one receive.** TCP is a byte stream with no message
   boundaries; sends can be split or coalesced. This protocol adds `\n` and
   buffers per connection (see Task 1).
3. **Receive returns 0 / reset.** 0 means the peer closed its side in order
   (FIN). Reset means an RST (process killed, abortive close): `Receive` throws;
   both are treated as disconnect and cancel the client's orders.
4. **Producer–consumer in the server.** Producers: client-handler threads that
   accept orders and put them in the queue. Consumers: worker threads that take
   orders and prepare them. Queue: the FIFO order queue.
5. **Cost of a thread waiting for a client; 1000 clients.** One OS thread plus
   its stack (about 1 MB reserved) and scheduler/context-switch cost. 1000
   clients would mean 1000 threads, which is heavy; an event loop (`select`,
   `epoll`, async I/O) would scale better.
6. **Race condition without locks.** See Task 2 question 2: two threads read the
   same stock value, both decrement, both accept; the last item is sold twice.
7. **What identifies a TCP connection; five clients on one port.** The 4-tuple
   (client IP, client port, server IP, server port). Each client uses a different
   ephemeral source port, so the server can accept all five on the same local
   port.

## Bonus status

- Two computers: the server binds `0.0.0.0`; clients take host/port arguments. ✔
- Two languages: Rust and Go clients/server included. ✔
- Stock saved on server restart: not implemented.
