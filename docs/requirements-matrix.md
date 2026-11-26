# Lab requirements matrix (Labs 2-4)

Checked against `resources/tasks/Lab2-CoffeeShop.txt`,
`Lab3-LANChat.txt`, and `Lab4-Broker.txt` on 2026-11-26.

Status: **Pass** means the requirement is implemented and has evidence in the
repository. **Pass with caveat** means the requirement is implemented but there
is an interpretation or demonstration risk to explain at the defense.

## Lab 2 - A Coffee Shop Server

| Lab sheet requirement | Status | Where |
|---|---|---|
| Direct TCP socket API; no `TcpListener`, `TcpClient`, `NetworkStream` | Pass | `Lab2/csharp/Server/Program.cs`; grep found no forbidden wrappers |
| Server, client, provider; client/provider one program in either role | Pass | `Lab2/csharp/Client/Program.cs`, `Lab2/go/client`, `Lab2/rust/client` |
| Port and host as command-line parameters | Pass | all clients/servers |
| Server logs connections, accepted/refused/ready orders, deliveries, errors with endpoint | Pass | `Program.cs` log calls; `server-desktop.txt` |
| Random stock 10-50 per product | Pass | C# `rng.Next(10, 51)`; Go `Intn(41)+10`; Rust `% 41 + 10` |
| Coffee 4 s, tea 3 s, croissant 2 s | Pass | `PrepTime` maps in all three servers |
| One worker per logical processor | Pass | C# `Environment.ProcessorCount` (12 on test machine), Go `runtime.NumCPU`, Rust `available_parallelism` |
| FIFO queue; one whole order per worker | Pass | blocking queue / channel / queue + workers |
| Shared stock protected; last-item race impossible | Pass | `StockLock` / Go mutex; race test evidence |
| Order refused if quantity exceeds stock, with remaining amount | Pass | `order_refused` response |
| Unknown product or non-positive quantity gets an error | Pass | `SendError` / Go `sendErr` / Rust `send_err` |
| Provider delivers products; client deliver gets an error | Pass | `deliver` handlers |
| Alert when a product falls below 5; alert on provider reconnect | Pass | `CheckAlerts` / `SendAlerts` |
| Client disappearance cancels queued and in-progress orders; products returned | Pass | `Claimed` + cancellation; new run: 4 queued orders cancelled/refunded |
| Provider disappearance does not stop the shop | Pass | provider removal |
| Invalid input gets an error; server never crashes | Pass | error paths; logs |
| Client/provider display messages while typing; clean exit on server loss | Pass | receiver thread/goroutine; exit code 0 |
| Git repository and build/run instructions | Pass | `README.md`, `Lab2/README.md` |
| Bonus: two computers | Pass | `Lab2/evidence/two-desktop-2026-11-26/`, phone run |
| Bonus: stock survives restart | Pass | `Lab2/evidence/stock-persistence-2026-11-26/` (C# and Go) |
| Bonus: two languages | Pass | C# server + Go clients; Rust also present |

Caveat: the new two-desktop run focuses on concurrency and disconnect refunds;
the last-item race and low-stock alerts are code-verified and covered by the
earlier evidence, so demonstrate them live.

## Lab 3 - A Local Network Chat

| Lab sheet requirement | Status | Where |
|---|---|---|
| Direct UDP socket API; no `UdpClient` | Pass | C# `SocketType.Dgram`; Go `net.UDPConn`; grep found no `UdpClient` |
| One peer program; name/port/passphrase CLI; all peers same port | Pass | `Lab3/csharp`, `Lab3/go` |
| Several peers on one computer and on different computers | Pass | 3 peers / 2 desktops, two peers share port 25565 on CachyOS |
| Log every sent/received/dropped datagram with time, endpoint, kind, size | Pass | `chat-*.log`; C# invalid-JSON drop now logs real endpoint/size |
| Invalid datagram dropped and logged; no crash | Pass | random, 600-char and 3000-byte test |
| Message at most 500 characters | Pass | C# `CharCount`, Go `utf8.RuneCountInString` |
| General channel: one datagram reaches all; author sees it once | **Pass with caveat** | one logical message, but sent to directed broadcast(s), limited broadcast, and loopback for multi-homed/local peers; deduplicated by `from:id` |
| Several peers on one computer receive the general channel | Pass | loopback broadcast fix |
| Discovery: join/hello within one second; heartbeat 5 s; gone after 15 s | Pass | `HeartbeatLoop`, `heartbeatLoop` |
| Unique names; duplicate told and does not join | Pass | `nameinuse` |
| `/users`, `/quit` leave notice | Pass | transcripts |
| Private message to one receiver only; offline name gets an error | Pass | private socket + peer lookup |
| Private messages encrypted: KDF + AEAD; general not encrypted | Pass | PBKDF2-SHA256 + AES-256-GCM; `privenc` logs |
| Bonus: two languages talking to each other | Pass | C# peer + Go peers, encrypted private both ways |

Caveat: the "single datagram" wording is the main interpretation risk. Be ready
to explain the multi-homed/local-peer reason, or use multicast if the teacher
insists.

## Lab 4 - A Message Broker

| Lab sheet requirement | Status | Where |
|---|---|---|
| HTTP broker on standard library; no web framework | Pass | C# `HttpListener`; Go `net/http` |
| gRPC broker with shared `.proto`, generated stubs | Pass | `Lab4/part2-grpc/proto/broker.proto`; C#/Go stubs |
| Broker, publisher, subscriber; console; dynamic count; same/different computers | Pass | both parts, two-desktop evidence |
| Broker serves clients concurrently; shared data protected | Pass | topic locks, delivery queue/channel |
| Request that cannot be accepted gets an error and is logged; no crash | Pass | 400/401/403 and gRPC status logs |
| Log every request and delivery with time, client, topic/id, result | Pass | C# HTTP request + publish/delivery logs; gRPC publish/delivery + ASP.NET request logs |
| Publish returns 202 at once; queue + background delivery in order | Pass | `HandlePublish`, delivery loop |
| Subscriber gone does not stop delivery; forgotten on write failure | Pass | HTTP heartbeat/context; gRPC `Subscribers.Remove` on write failure (fixed) |
| Ids start at 1, consecutive, unique with two publishers | Pass | topic lock; evidence ids 1-10 |
| Two subscribers receive every message in order | Pass | C# and Go subscriber transcripts |
| Restart keeps messages and continues ids; temp file + rename | Pass | id 12, 12 messages; `Persist` |
| HTTP tokens: 401 + `WWW-Authenticate`, 403, 202 | Pass | `auth.txt` |
| gRPC tokens: `Unauthenticated`, `PermissionDenied`, OK | Pass | `auth.txt` |
| Token owner stored as sender, not client claim | Pass | C# HTTP/gRPC and Go HTTP/gRPC (Go gRPC fixed) |
| gRPC part has no HTTP API | Pass | only `MapGrpcService` |
| Two publishers and two subscribers on two computers | Pass | `Lab4/evidence/two-desktop-2026-11-26/` |

Caveat: the recorded C# and Go publisher bursts did not interleave perfectly;
the per-topic lock guarantees unique ids. Start both publishers truly
simultaneously at the defense to show interleaving.

## Bonuses

- **Lab 2:** all three bonuses (two computers, stock persistence, two
  languages).
- **Lab 3:** two-language bonus.
- **Lab 4:** no bonus in the lab sheet. C# and Go implementations are extra
  cross-language compatibility, not a graded bonus.

## Remaining risks

1. **Network:** at the time of this audit the two desktops are on different
   Wi-Fi networks (`MOLDTELECOM_069` at 192.168.1.33 and `Vermilion:2.4` at
   192.168.1.22). Connect both to the same SSID/hotspot before the defense.
2. **Lab 3 "single datagram":** see the caveat above.
3. **Live defense:** the teacher can point at any code and ask for a small
   change. Use `KNOWLEDGE.md` and practice the live edits.
4. **C# HTTP broker on the LAN:** `HttpListener` needs an administrator URL ACL;
   the Go broker is the LAN alternative.
5. **Lab 3 port:** 25565/UDP is used because the Linux host's UFW allows it;
   open 9000/UDP if the teacher requires that exact port.
