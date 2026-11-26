# Lab 4 - cross-device run (2026-11-26)

Desktop **192.168.1.33** (Windows), phone **192.168.1.5** (Android, Go arm64).

## Part 1 - HTTP

Broker: **Go HTTP broker on the desktop, port 8080** (`http://192.168.1.33:8080`).
The C# `HttpListener` broker needs an administrator URL ACL for a non-localhost
prefix (see `Lab4/README.md`), so the cross-machine HTTP evidence uses the Go
broker. The C# publisher and subscriber are still used, so this also proves the
two implementations agree on the HTTP API.

- 2 publishers: **Go on the phone** (token `tok-phone`) and **C# on the desktop**
  (token `tok-teacher`), publishing at the same time to `grades`.
- 2 subscribers: **Go on the phone** and **C# on the desktop**.
- Both subscribers received all **11 messages, in order, ids 1-11, no loss**
  (5 desktop + 5 phone + 1 authenticated phone message).
- Auth: no token -> **401**, phone token to `news` -> **403**, phone token to
  `grades` -> **202**.
- Restart: the broker was stopped and started again; the next publish got
  **id 12** and the earlier messages were reloaded from disk.

| File | What it is |
|---|---|
| `desktop-broker-go-192.168.1.33.txt` | broker log (every request, subscriber and delivery) |
| `desktop-broker-go-restart-192.168.1.33.txt` | log after the restart |
| `phone-clients-192.168.1.5.txt` | phone subscriber + publisher + auth tests + post-restart publish |
| `desktop-subscriber-csharp-192.168.1.33.txt` | desktop C# subscriber transcript |
| `desktop-publisher-csharp-192.168.1.33.txt` | desktop C# publisher transcript |
| `server-messages.json` | persisted messages after the restart test |

## Part 2 - gRPC

Broker: **C# / .NET 10 gRPC broker on the desktop, port 5000**.
Publishers: Go on the phone + C# on the desktop. Subscribers: Go on the phone +
C# on the desktop. Shared contract: `Lab4/part2-grpc/proto/broker.proto`.

- **11 messages**, ids 1-11, both subscribers received every message in order.
- Auth via `authorization` metadata: no token -> **Unauthenticated**, phone
  token to `news` -> **PermissionDenied**, phone token to `grades` -> **OK**.
- With auth enabled the broker stores the **token owner** as sender
  (`teacher`/`phone`), not a client-supplied name.
- Restart: next id **12**, messages persisted in `broker-data/grades/messages.json`.

| File | What it is |
|---|---|
| `desktop-broker-csharp-192.168.1.33.txt` | C# broker log |
| `desktop-broker-csharp-restart-192.168.1.33.txt` | C# broker log after restart |
| `phone-clients-192.168.1.5.txt` | phone Go subscriber, publishers, auth tests, post-restart publish |
| `desktop-subscriber-csharp-192.168.1.33.txt` | desktop C# subscriber transcript |
| `desktop-subscriber-csharp-transcript-192.168.1.33.txt` | same transcript, captured separately |
| `desktop-publisher-csharp-192.168.1.33.txt` | desktop C# publisher transcript |
| `server-messages.json` | persisted messages after the restart test |

## Reproduce

```powershell
# Part 1 (desktop broker, phone/pub on the LAN)
go run ./broker -addr :8080          # from Lab4/part1-http/go, cwd with tokens.txt
./bin/arm64/lab4http-subscriber http://192.168.1.33:8080 grades
./bin/arm64/lab4http-publisher  http://192.168.1.33:8080 grades Phone tok-phone

# Part 2 (desktop C# broker, port 5000)
dotnet run --project Lab4/part2-grpc/broker
./bin/arm64/lab4grpc-subscriber 192.168.1.33:5000 grades
./bin/arm64/lab4grpc-publisher  192.168.1.33:5000 grades Phone tok-phone
```

`tokens.txt` (ASCII):

```text
tok-teacher teacher grades,news
tok-phone phone grades
```
