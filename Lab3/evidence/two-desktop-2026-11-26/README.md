# Lab 3 - three peers, two desktops (2026-11-26)

Three peers on two computers, all on the same Wi-Fi network:

| Peer | Device | IP | Implementation | Role |
|---|---|---|---|---|
| Cara | Windows desktop `DESKTOP-8MKK1R0` (Dinu Plesu) | 192.168.1.41 | C# / .NET 10 | one peer |
| Alice | Igor Boltunov's CachyOS desktop `Cupcake` | 192.168.1.22 | Go 1.27.4 | peer on the shared port |
| Bob | Igor Boltunov's CachyOS desktop `Cupcake` | 192.168.1.22 | Go 1.27.4 | second peer on the same shared port |

Chat port: **25565/UDP** (same for all peers), passphrase: `secret`.

The port is 25565, not 9000, because the Linux desktop runs UFW with the
default input policy `DROP`; 25565/UDP is already allowed, 9000/UDP was not.
The task lets the chat use any common port.

## What was exercised

- **Discovery**: all three peers saw each other within about a second.
- **General channel**: one logical message from each peer reached all three,
  once; the two peers sharing port 25565 on the Linux computer both received it.
- **Private messages**: Alice -> Bob, Alice -> Cara, Bob -> Cara and Bob ->
  Alice. Only the receiver saw the message. The logs show `privenc` on the wire,
  so the private payloads were AES-256-GCM encrypted with the PBKDF2 key.
- **`/users`**: all three peers listed on all three consoles.
- **`/quit`**: Cara's leave was shown by Alice and Bob.
- **Duplicate name**: a second `Alice` was told
  `ERROR: name 'Alice' already in use` and did not join.
- **Invalid/oversized input**: 14 random bytes -> `DROP ... invalid_json`;
  a JSON `gen` with 600 characters -> `DROP ... gen_too_long`;
  a 3000-byte datagram -> `DROP ... too_long`. No peer crashed.

## Fix found during the run

A broadcast sent out the Wi-Fi card is not delivered back to the other local
sockets. The first run (port 9000) showed the two Linux peers did not receive
each other. The fix, in both C# and Go, is to send the general/discovery
datagram also to the loopback broadcast **127.255.255.255**. A Go test with two
sockets bound to the same port confirmed that both receive that copy, while they
do not receive the `192.168.1.255` copy.

The C# peer also disables `SIO_UDP_CONNRESET` and logs oversized datagrams as
`too_long`; the Go peer already had the reuse and log handling.

## Files

| File | What it is |
|---|---|
| `linux-peers-25565/alice.txt` | Alice console: discovery, general, `/users`, private, leave |
| `linux-peers-25565/bob.txt` | Bob console: only the private messages addressed to Bob |
| `linux-peers-25565/chat-Alice-25565.txt` | Alice datagram log (`SEND`/`RECV`, `privenc`) |
| `linux-peers-25565/chat-Bob-25565.txt` | Bob datagram log |
| `windows-csharp-peer-25565/cara-console.txt` | Cara console (C#) |
| `windows-csharp-peer-25565/cara-chat.txt` | Cara datagram log (`privenc`/`private`) |
| `duplicate-name/` | second Alice is refused; host transcript and log |
| `invalid-datagrams/` | random, 600-character and 3000-byte datagrams dropped |

## Reproduce

```powershell
# Windows desktop, C#
dotnet run --project Lab3/csharp/Chat.csproj -- Cara 25565 secret
```

```bash
# Linux desktop, two Go peers on the same port
cd Lab3/go
go build -o lab3-chat .
./lab3-chat Alice 25565 secret
./lab3-chat Bob   25565 secret
```

The transcripts were captured with timed stdin pipes, for example:

```bash
( sleep 5; echo "hello from Alice on linux"; sleep 1; echo /users; \
  sleep 1; echo "/msg Bob private from Alice"; sleep 5; echo /quit ) \
  | ./lab3-chat Alice 25565 secret
```
