# Lab 3 - cross-device run (2026-11-26)

Two peers, two computers, same Wi-Fi network:

| Peer | Device | IP | Implementation | Private port |
|---|---|---|---|---|
| Phone | Android (Acode/Alpine, arm64) | 192.168.1.5 | Go, static arm64 binary | 57749 |
| Desktop | Windows, i5-13420H | 192.168.1.33 | C# / .NET 10 | 63595 |

Chat port: **9000**, passphrase: `secret`.

## What was exercised

- Peer discovery: both sides answered the join and appeared in `/users`.
- General channel: one UDP broadcast datagram per message, received by both
  peers; own message shown once (seen-ID dedup).
- Private messages: only to the receiver's private endpoint, encrypted with
  AES-GCM (PBKDF2 key from the passphrase); the logs show `private`/`privenc`.
- Leave: `/quit` broadcast the bye, the other side showed the leave.
- Datagram log: every send/receive with timestamp, endpoint, kind and size.

## Important fix found during this run

Windows has several interfaces (Wi-Fi **192.168.1.33** plus Hyper-V
**192.168.64.1**). Sending only to `255.255.255.255` left the machine through
the Hyper-V card, so the phone never received the desktop's broadcasts and the
two peers could not see each other.

Fixes in both peers (C# and Go):

1. send the general-channel datagram to **every interface's directed broadcast**
   (for example `192.168.1.255`) plus `255.255.255.255`;
2. when a join/hello/heartbeat arrives, take the peer's private port from the
   message but the **IP from the datagram source**, so a multi-homed host that
   advertises the wrong card is still reachable for private messages.

## Verification from the logs

Phone log:

- `RECV 192.168.1.33:9000 general ...` - desktop general messages and heartbeats.
- `RECV 192.168.1.33:63595 private 153` - encrypted private message from the desktop.
- `SEND 192.168.1.33:63595 privenc 149` - encrypted private message to the desktop.

Desktop log:

- `RECV 192.168.1.5:9000 general ...` - phone general messages and heartbeats.
- `SEND 192.168.1.5:57749 privenc 153` and `RECV 192.168.1.5:57749 private 149` -
  encrypted private message in both directions.

Console transcripts (both sides) show:

```text
Online:
  Desktop 192.168.1.33:63595
  Phone   192.168.1.5:57749
[general] Desktop: hello from the desktop general channel
[general] Phone: hello from the phone general channel
[private] Desktop: private message from desktop
[private] Phone: private message from phone
```

Files:

- `phone-9000-192.168.1.5.txt` - complete datagram log of the Go peer on the phone.
- `desktop-9000-192.168.1.33.txt` - complete datagram log of the C# peer on the desktop.

Reproduce:

```powershell
# desktop
dotnet run --project Lab3/csharp/Chat.csproj -- Desktop 9000 secret
# phone (arm64 build)
./bin/arm64/lab3-chat Phone 9000 secret
```

