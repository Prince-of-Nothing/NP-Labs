# Lab 4 - two-desktop run (2026-11-26)

Brokers on Dinu Plesu's Windows desktop (192.168.1.41); publishers and
subscribers on both desktops. The Linux desktop is Igor Boltunov's `Cupcake`
(CachyOS, 192.168.1.22). The brokers are on the Windows side because the Linux
desktop's UFW drops inbound connections by default; all Linux clients connect
outbound.

For each part: **2 publishers and 2 subscribers** at the same time, one C# and
one Go on each side. Tokens:

```text
tok-teacher teacher grades,news
tok-phone phone grades
```

The file is also stored as `part1-http/tokens-used.txt` and
`part2-grpc/tokens-used.txt` (the broker itself expects `tokens.txt`).

## Part 1 - HTTP

Broker: **Go HTTP broker on Windows, port 8080**. C# publisher/subscriber on
Windows; Go publisher/subscriber on Cupcake.

- Publishers: C# `teacher` + Go `phone`, five messages each, at the same time.
- **Ids 1-10 unique**; both subscribers received all ten messages, in order.
- HTTP auth: no token -> **401 Unauthorized + `WWW-Authenticate: Bearer`**;
  `tok-phone` to `news` -> **403 Forbidden**; `tok-phone` to `grades` ->
  **202 Accepted**, id 11.
- With auth on, the stored sender is the **token owner** (`phone`), not the
  client-supplied name. The Go broker was fixed during this run to match the
  C# behaviour.
- Restart: broker stopped and started again in the same working directory; the
  next publish got **id 12**, and all 12 messages were loaded from disk
  (`server-messages.json`).

## Part 2 - gRPC

Broker: **C# / .NET 10 gRPC broker on Windows, port 5000**. C# publisher and
subscriber on Windows; Go publisher and subscriber on Cupcake.

- Publishers: C# `teacher` + Go `phone`, five messages each.
- **Ids 1-10 unique**; both subscribers received all ten, in order.
- Auth via `authorization` metadata: no token -> **Unauthenticated**;
  `tok-phone` to `news` -> **PermissionDenied**; `tok-phone` to `grades` ->
  **OK**, id 11. The broker stores the token owner as sender.
- Restart: next publish got **id 12**; all 12 messages persisted in
  `broker-data/grades/messages.json` (`server-messages.json`).
- Part 2 has **no HTTP API**; only the gRPC contract.

## Files

| File | What it is |
|---|---|
| `part1-http/broker.txt` | HTTP broker log: requests, subscribers, deliveries |
| `part1-http/desktop-csharp-publisher.txt` | C# publisher: 202 responses and ids |
| `part1-http/desktop-csharp-subscriber.txt` | C# subscriber: all ten messages in order |
| `part1-http/linux-go-publisher.txt` | Go publisher on Cupcake: 202 responses and ids 6-10 |
| `part1-http/linux-go-subscriber.txt` | Go subscriber on Cupcake: all ten messages in order |
| `part1-http/auth.txt` | 401 + `WWW-Authenticate`, 403, 202 |
| `part1-http/restart-broker.txt` | broker log after restart |
| `part1-http/restart-publish.txt` | post-restart publish, id 12 |
| `part1-http/server-messages.json` | persisted messages after the restart |
| `part2-grpc/broker.txt` | gRPC broker log before the restart |
| `part2-grpc/broker-before-restart.txt` | full pre-restart log |
| `part2-grpc/desktop-csharp-publisher.txt` | C# publisher: `Published id=...` |
| `part2-grpc/desktop-csharp-subscriber.txt` | C# subscriber: all ten messages |
| `part2-grpc/linux-go-publisher.txt` | Go publisher on Cupcake: ids 6-10 |
| `part2-grpc/linux-go-subscriber.txt` | Go subscriber on Cupcake: all ten messages |
| `part2-grpc/auth.txt` | Unauthenticated / PermissionDenied / OK |
| `part2-grpc/restart-broker.txt` | broker log after restart |
| `part2-grpc/restart-publish.txt` | post-restart publish, id 12 |
| `part2-grpc/server-messages.json` | persisted messages after the restart |

## Reproduce

```powershell
# HTTP broker (Go) on Windows, from Lab4/part1-http/go
go build -o broker ./broker
./broker -addr :8080

# gRPC broker (C#) on Windows
dotnet run --project Lab4/part2-grpc/broker
```

```bash
# Linux Go clients, from Lab4/part1-http/go and part2-grpc/go
./subscriber http://192.168.1.41:8080 grades
./publisher  http://192.168.1.41:8080 grades phone tok-phone

./subscriber 192.168.1.41:5000 grades
./publisher  192.168.1.41:5000 grades phone tok-phone
```

The C# and Go publishers read one line from stdin per message. The subscribers
were stopped after the ten messages were delivered; the restart publish was made
with the C# publisher.
