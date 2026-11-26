# Lab 3 — LAN Chat (UDP)

Defense notes and control-question answers: [KNOWLEDGE.md](./KNOWLEDGE.md).
Written report: [report/report.pdf](./report/report.pdf) (LaTeX source in
[report/report.tex](./report/report.tex)).

Serverless peer-to-peer chat over raw UDP sockets. One program, every running
copy is a peer. No server, no chat/messaging library, no service discovery
library.

## Layout

- `csharp/` — the C# chat peer (`Chat.csproj`).
- `go/` — the Go chat peer (stdlib only).

## Build and run

C#:

```powershell
dotnet run --project csharp/Chat.csproj -- <name> <port> [passphrase]
```

Go:

```powershell
cd go
go run . <name> <port> [passphrase]
```

Example (three peers on the same machine or LAN):

```powershell
dotnet run --project csharp/Chat.csproj -- Alice 9000 secret
dotnet run --project csharp/Chat.csproj -- Bob   9000 secret
dotnet run --project csharp/Chat.csproj -- Cara  9000 secret
```

## Commands

- `text` — send to the general channel.
- `/msg name text` — private message (encrypted when a passphrase is given).
- `/users` — show peers currently online.
- `/quit` — leave (broadcasts goodbye).

## Behaviour

- General channel: one UDP broadcast datagram reaches every peer (subnet +
  limited broadcast), author shown once.
- Discovery: join broadcast; existing peers answer immediately; heartbeat every
  5 s; peer considered gone after 15 s.
- Names are unique; a duplicate name is told so and does not join.
- Private messages go only to the receiver's unique private port, not the
  shared chat port, so no other peer (even on the same machine) receives them.
- Task 4: PBKDF2-derived key from the passphrase; AES-GCM with a random nonce
  per message (confidentiality + integrity). General channel is not encrypted.
- Every datagram is logged (`chat-<name>-<port>.log`): time, endpoint, kind,
  size; invalid/oversized datagrams are dropped and logged. Max 500 characters.
- Incoming messages are shown while the user types (separate receive threads).

## Docker

```powershell
docker compose run --rm chat-alice
docker compose run --rm chat-bob
```

Lab 3 uses `network_mode: host` in compose so UDP broadcast works.

## Verified

Discovery (simultaneous and staggered starts), general channel, `/users`,
`/quit` leave notice, duplicate-name rejection, encrypted private messages,
invalid datagrams dropped without crashing.


## Cross-device evidence (2026-11-26)

Go peer on Android (192.168.1.5) + C# peer on Windows (192.168.1.33): discovery,
general channel, encrypted private messages, leave, and complete datagram logs.
See [Lab3/evidence/cross-device-2026-11-26/](./evidence/cross-device-2026-11-26/README.md).
The same-port requirement (three peers on one computer) is verified in
[Lab3/evidence/local-multi-peer-2026-11-26/](./evidence/local-multi-peer-2026-11-26/README.md).


## Two-desktop evidence (2026-11-26)

Three peers on two real desktops: **Cara** (C#, Windows, 192.168.1.41) and
**Alice + Bob** (Go, CachyOS, 192.168.1.22) on port **25565/UDP**, passphrase
`secret`. All three discovered each other, exchanged general messages, `/users`
and leave notices, and exchanged AES-GCM private messages in both directions
(only the receiver saw each one). A second `Alice` was refused, and random,
600-character and 3000-byte datagrams were dropped and logged.

The run found a real same-computer issue: a broadcast sent out the Wi-Fi card
does not come back to the other local sockets. Both implementations now also
send to the loopback broadcast **127.255.255.255**. Port 25565 was used because
the Linux desktop's UFW already allows it; 9000/UDP is blocked by the default
input policy. See
[Lab3/evidence/two-desktop-2026-11-26/](./evidence/two-desktop-2026-11-26/README.md).

