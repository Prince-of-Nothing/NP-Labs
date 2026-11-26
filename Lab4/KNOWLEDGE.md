# Lab 4 — Message Broker: implementation notes and defense answers

## What is implemented

Two independent implementations of the same broker:

- **Part 1 HTTP** (`part1-http/`, C# with `HttpListener`, Go with `net/http`).
- **Part 2 gRPC** (`part2-grpc/`, C# with `Grpc.AspNetCore`, Go with
  `google.golang.org/grpc`), one shared `proto/broker.proto`.

Publishers and subscribers know only the broker. Every topic keeps its messages
in order; ids start at 1 and grow by one. Publish queues and returns at once; a
background delivery task writes to every subscriber of the topic.

## HTTP API (Part 1)

| Request | Result |
|---|---|
| `POST /topics/{topic}/messages` | 202 Accepted + message (`id`, `topic`, `sender`, `body`, `time`) |
| `GET /topics/{topic}/stream` | 200 OK, open response, one JSON message per line |
| `GET /topics` | 200 OK, topics + sizes |

- Tokens file lines: `<token> <publisher> <topic1,topic2>`.
- Missing/unknown token → 401 + `WWW-Authenticate: Bearer`; wrong topic → 403.
- Persistence: `broker-data/<topic>/messages.json`, written to `.tmp` then moved
  into place; ids resume from max(id)+1.

## gRPC contract (Part 2)

```proto
service Broker {
  rpc Publish (PublishRequest) returns (PublishReply);
  rpc Subscribe (SubscribeRequest) returns (stream Message);
  rpc ListTopics (ListTopicsRequest) returns (ListTopicsReply);
}
```

- Publish unary, Subscribe server-streaming, ListTopics unary.
- Same topics/messages/ids/persistence as Part 1; no HTTP API in this part.
- Token in `authorization` metadata (`Bearer <token>`); missing/unknown →
  `Unauthenticated`, topic not allowed → `PermissionDenied`.

## Verified

- Two publishers, one topic: 20 messages, ids 1–20 unique, all delivered, no loss.
- Two subscribers: both receive every message, in order.
- Subscriber gone: removed, delivery to the others continues.
- Restart: stored messages still there, next id continues (1,2 → restart → 3).
- HTTP auth 401/403/202; gRPC Unauthenticated/PermissionDenied/OK.
- Go and C# interoperate: Go publisher/subscriber against the C# broker and
  C# publisher/subscriber against the Go broker; `ListTopics` returns sizes on
  both brokers.

## Why these choices

**Why 202 Accepted and not 200?** The request is accepted and queued; delivery to
subscribers happens later in the background. 202 says "accepted for processing",
not "already delivered to everyone".

**Why a queue plus a background delivery task?** It decouples publisher latency
from subscriber speed. A slow subscriber must not block a publish. The queue keeps
per-topic order; one delivery task consumes messages in order and fans them out.

**How are ids unique with two publishers at the same time?** Each topic has a
lock; `NextId++`, append and timestamp happen inside it. Two publishers therefore
get consecutive, unique ids. The topic map has its own lock because new topics
can be created concurrently.

**How does one publish reach several subscribers?** The delivery task keeps a
list of open subscriber streams/channels per topic, snapshots it, and writes the
message to each. A failed write removes that subscriber; the others continue.

**How is a gone subscriber detected?** HTTP: a periodic heartbeat write (and any
message write) fails → subscriber marked dead and removed. gRPC: the request's
context is cancelled → the Subscribe handler returns and removes the stream.

**How does a restart lose nothing?** After every accepted message the topic is
serialized to a temporary file and moved over `messages.json`. On startup the
broker loads every topic file and sets `NextId = max(id)+1`, so old messages and
id continuity survive a kill. A crash during the write leaves the old file intact
because the replacement is a rename.

**Why is the broker not an existing broker?** The lab requires the broker itself:
only the standard HTTP server/client (Part 1) and gRPC (Part 2); RabbitMQ, Kafka,
Redis, NATS, etc. are forbidden.

**What is logged?** Every request (method, path, client), every publish (id,
topic, sender, client), every delivery (id, topic) and subscriber add/remove,
with the result.

**HTTP vs gRPC.** HTTP/1.1 with JSON and hand-written paths/status codes versus a
typed `.proto` contract, generated stubs, binary protobuf over HTTP/2 and a
server-streaming call. The gRPC version has no HTTP API; the contract replaces
the table of methods and fields.

**Do subscribers get old messages after reconnect?** No. `Subscribe` delivers new
messages as they arrive, as the lab requires. Persistence is for surviving a
broker restart and continuing ids, not for replaying history to a new subscriber.

**Slow subscriber.** HTTP subscribers are removed when a write fails; the Go
implementation drops a subscriber whose buffered channel is full. Delivery to
the remaining subscribers continues; one slow client cannot stall the broker.

**Token errors.** HTTP uses 401 for "no/unknown token" and 403 for "valid token,
topic not allowed". gRPC uses `Unauthenticated` and `PermissionDenied` for the
same two cases.

## Layout

- `part1-http/broker`, `part1-http/publisher`, `part1-http/subscriber`
- `part1-http/go/{broker,publisher,subscriber}`
- `part2-grpc/broker`, `part2-grpc/publisher`, `part2-grpc/subscriber`
- `part2-grpc/go/{broker,publisher,subscriber}`, `part2-grpc/go/pb` (generated)
- `part2-grpc/proto/broker.proto` (shared contract)

## Cross-device notes (2026-11-26)

- `tokens.txt` may start with a UTF-8 BOM (Notepad/PowerShell); the Go brokers
  strip it before parsing, so the first token does not become unknown (401).
- With authentication on, the broker records the **token owner** as the sender,
  not a client-supplied name; topic names are validated so they cannot escape
  the `broker-data` directory.
- Cross-device evidence: HTTP (Go broker on the desktop, Go phone + C# desktop
  clients) and gRPC (C# broker, Go phone + C# desktop clients), 2 publishers and
  2 subscribers, auth and restart checks:
  [evidence/cross-device-2026-11-26/](./evidence/cross-device-2026-11-26/README.md).
- On Windows, run `enable-lan-http.ps1` once as Administrator to allow the C#
  `HttpListener` broker on `http://+:8080/`.
