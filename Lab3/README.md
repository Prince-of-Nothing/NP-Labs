# Lab 3 — LAN Chat (UDP)

Serverless peer-to-peer chat over raw UDP sockets. One program, every running
copy is a peer. No server, no chat/messaging library, no service discovery
library.

## Layout

- `csharp/` — the chat peer (`Chat.csproj`).

## Build and run

```powershell
dotnet run --project csharp/Chat.csproj -- <name> <port> [passphrase]
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
