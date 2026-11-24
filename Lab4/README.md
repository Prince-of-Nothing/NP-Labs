# Lab 4 — Message Broker (HTTP + gRPC)

Defense notes and design answers: [KNOWLEDGE.md](./KNOWLEDGE.md).

A message broker between publishers and subscribers. Publishers know only the
broker; subscribers know only the broker. Two parts, same tasks.

## Layout

- `part1-http/broker` — HTTP broker on `HttpListener` (no web framework).
- `part1-http/publisher`, `part1-http/subscriber` — console clients.
- `part2-grpc/broker` — gRPC broker (`Grpc.AspNetCore`).
- `part2-grpc/publisher`, `part2-grpc/subscriber` — gRPC console clients.
- `part2-grpc/proto/broker.proto` — shared contract.
- `part1-http/go`, `part2-grpc/go` — Go implementations (stdlib HTTP,
  `google.golang.org/grpc`).

## Part 1 — HTTP

```powershell
dotnet run --project part1-http/broker -- http://localhost:8080/
dotnet run --project part1-http/subscriber -- http://localhost:8080 grades
dotnet run --project part1-http/publisher -- http://localhost:8080 grades teacher [token]
```

API:

| Request | Result |
|---|---|
| `POST /topics/{topic}/messages` | 202 Accepted, message with id/time |
| `GET /topics/{topic}/stream` | 200 OK, open response with new messages |
| `GET /topics` | 200 OK, topics and sizes |

- Publish queues the message and answers at once; a background delivery task
  writes it to every subscriber of the topic, in order.
- A subscriber that disappears does not stop delivery to the others (detected
  with a heartbeat write, removed on failure).
- Restart: messages are stored per topic in `broker-data/`; atomic temp file +
  rename. Ids continue after restart.
- Tokens: `tokens.txt` lines are `<token> <publisher> <topic1,topic2>`.
  Missing/unknown token → 401 + `WWW-Authenticate: Bearer`; wrong topic → 403.
- Default prefix is `http://localhost:8080/` so it runs without Administrator.
  To accept LAN connections on Windows, run once as Administrator:
  `netsh http add urlacl url=http://+:8080/ user=Everyone` and start the broker
  with `http://+:8080/` (on Linux/Docker `http://+:8080/` works directly).

Go (from `part1-http/go`):

```powershell
go run ./broker -addr :8080
go run ./subscriber http://localhost:8080 grades
go run ./publisher http://localhost:8080 grades teacher [token]
```

## Part 2 — gRPC

```powershell
dotnet run --project part2-grpc/broker
dotnet run --project part2-grpc/subscriber -- http://localhost:5000 grades
dotnet run --project part2-grpc/publisher -- http://localhost:5000 grades teacher [token]
```

- `Publish` unary, `Subscribe` server-streaming, `ListTopics` unary.
- No HTTP API in this part (only gRPC).
- Same persistence and token rules; token travels in `authorization` metadata.
- Missing/unknown token → `Unauthenticated`; topic not allowed → `PermissionDenied`.

Go (from `part2-grpc/go`, generated stubs in `pb/`):

```powershell
go run ./broker -addr :5000
go run ./subscriber localhost:5000 grades
go run ./publisher localhost:5000 grades teacher [token]
```

Regenerate the stubs after editing the proto:

```powershell
protoc --go_out=pb --go_opt=paths=source_relative `
       --go-grpc_out=pb --go-grpc_opt=paths=source_relative `
       -I ../proto -I <protoc-include> ../proto/broker.proto
```

## Docker

```powershell
docker compose up -d broker-http
docker compose run --rm subscriber-http
docker compose run --rm publisher-http

docker compose up -d broker-grpc
docker compose run --rm subscriber-grpc
docker compose run --rm publisher-grpc
```

## Verified

- Two publishers on one topic: 20 messages, ids 1–20 unique, no loss.
- Two subscribers: both receive both messages in order.
- Restart keeps messages and continues ids (1,2 → restart → 3).
- HTTP auth 401/403/202; gRPC Unauthenticated/PermissionDenied/OK.
