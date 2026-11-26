# Lab 3 — LAN Chat: implementation notes and defense answers

## What is implemented

- Raw UDP sockets only (C# `Socket`, Go `net.UDPConn`); no server.
- One program, every copy is a peer; CLI: `<name> <port> [passphrase]`.
- General channel: one datagram broadcast to the subnet and to 255.255.255.255.
- Discovery: join broadcast, immediate hello replies, heartbeat every 5 s, gone after 15 s.
- Unique names; a duplicate gets `nameinuse` and exits.
- Private messages: one datagram to the receiver's unique private port.
- Encryption: PBKDF2-SHA256 (100k iterations, 16-byte zero salt) → 32-byte key;
  AES-GCM with a random 12-byte nonce and 16-byte tag per message.
- Log file `chat-<name>-<port>.log`: time, direction, endpoint, kind, size.
- Invalid/oversized datagrams are dropped and logged; text limit 500 characters.
- C# and Go peers interoperate, including encrypted private messages.

## Verified

- 3 peers on one machine (C#, Go, C#): all discover each other, general and
  private messages work, `/users`, `/quit` leave notices.
- C# ↔ Go encrypted private messages decrypt correctly.
- Invalid bytes / non-JSON / oversized datagrams dropped without a crash.

## Task 1

**1. Why bind 0.0.0.0 and not the IP or 127.0.0.1?**
`0.0.0.0` receives on every interface, so the chat works on Wi-Fi, hotspot,
Ethernet or USB tethering without knowing the address in advance. A specific IP
would bind only that interface; `127.0.0.1` would only see the same machine.

**2. How can several peers on one computer bind the same port? Which receive a general message?**
`SO_REUSEADDR` (Windows) and `SO_REUSEPORT` (Linux) let several UDP sockets bind
the same address/port. Broadcast/multicast datagrams are delivered to all of
them, so every local peer sees the general channel. A **unicast** to that shared
port is delivered to only one socket, which is why private messages use a
separate unique port.

**3. Your own message comes back; how is it recognized?**
The datagram carries `from` and an `id`. A peer ignores datagrams whose `from`
is its own name (it already printed the message locally), and also deduplicates
`from:id` because the general message is sent to both the subnet and the limited
broadcast address.

**4. Why at most 500 characters? What would happen at 100,000?**
A UDP datagram has a maximum payload of 65,507 bytes (IPv4), but real networks
fragment above the MTU (~1,400 bytes); large datagrams are often dropped. 500
UTF-8 characters stay well below that. 100,000 characters would exceed the
datagram limit, fail to send or be fragmented/dropped, and the receiver would
drop it as too long. Our send and receive paths both enforce 500.

## Task 2

**1. How does a new peer learn who is online, and how long?**
It broadcasts `join` with its name and private endpoint. Every online peer adds
it, replies with `hello`, and the newcomer waits about 1.2 s; each `hello` adds
a peer. So the list is complete within about a second.

**2. A peer process is killed. How and when do the others notice? Why not at once?**
UDP has no connection and no FIN/RST. Peers send `heartbeat` every 5 s and
remove a peer whose last heartbeat is older than 15 s. A killed process is thus
noticed after up to ~15 s. TCP would notice a closed connection at once because
the kernel sends FIN/RST.

**3. Two peers start with the same name at the same moment. What happens?**
Both broadcast `join`. Each sees a `join` with its own name but a different
private endpoint, sends `nameinuse` to that endpoint, and exits. In practice at
least one is rejected; the survivor keeps the name. The check is
`name == my name && endpoint != my endpoint`, so a peer's own loopback is not
mistaken for a conflict.

## Task 3

**1. Several peers on one computer share the chat port. How does a private message reach the right one?**
Every peer also binds a second socket to an ephemeral port (0.0.0.0:0) and
advertises that endpoint in `join`/`hello`. A private message is sent to that
unique endpoint, not to the shared chat port, so only the intended peer receives
it, even on the same machine.

**2. Why not send a private message to everybody with the receiver name inside?**
Everyone would receive and could read it; it also wastes bandwidth and breaks
the privacy requirement. Unicast to the receiver's unique endpoint keeps it
private.

**3. What happens to a private message sent to a peer that left a second ago?**
The sender's list may still contain it until the 15 s timeout. The datagram goes
to a closed port and is lost (the OS may return ICMP port unreachable). UDP has
no delivery confirmation, so the sender does not know unless an application-level
acknowledgement is added.

**4. UDP can lose a private message. How could the sender know, and what would it do?**
Add an application-level ACK: receiver sends `ack(id)`, sender retries with a
timeout and a retry limit; or use TCP. This lab does not implement ACKs, so a
lost private message is simply lost.

## Task 4

**1. Why derive the key with a KDF instead of using the passphrase bytes?**
A passphrase is low-entropy and has arbitrary length. PBKDF2 stretches it with
100,000 iterations to a fixed 32-byte key and makes brute-force guessing slower.
Using the raw bytes would give a weak, possibly wrong-length key.

**2. Which algorithm encrypts messages? Why must the nonce never repeat? Where do yours come from?**
AES-256-GCM. Reusing a nonce with the same key breaks GCM's confidentiality and
authentication guarantees. Each message uses a fresh random 12-byte nonce from a
CSPRNG; the nonce is sent with the ciphertext and tag.

**3. Every member knows the passphrase. Who can read Ana↔Ion? What would be needed for only the two?**
Any member who knows the passphrase can derive the key and read every private
message. To restrict it to Ana and Ion, they would need a per-pair key exchange
(e.g. authenticated Diffie–Hellman) and encrypt with that pair key, not a
chat-wide key.

**4. Can someone without the passphrase change an encrypted message unnoticed? What protects it?**
No. AES-GCM produces an authentication tag over the ciphertext; any change makes
`Open`/`Decrypt` fail, and the receiver logs a decryption failure and drops the
message. Without the key the attacker cannot forge a valid tag.

## Control questions

1. **Which TCP promises does UDP not make?** Connection setup, reliable delivery,
   ordering, no duplication, flow/congestion control, stream semantics. The chat
   rebuilt discovery/join/leave with heartbeats and timeouts, but not reliable
   delivery or ordering; it lives with loss and treats each datagram independently.
2. **Why is one `sendto` one `recvfrom` with UDP, while one `send` is not one
   `receive` with TCP?** UDP preserves datagram boundaries: each `sendto` is one
   datagram. TCP is a byte stream with no message boundaries. The chat therefore
   needs no length prefix or delimiter; JSON gives the internal structure.
3. **How big can a UDP datagram be? Why stay below ~1,400 bytes?** Maximum IPv4
   UDP payload is 65,507 bytes. Above the path MTU (~1,500 bytes, ~1,400 safe)
   the IP layer fragments; one lost fragment loses the whole datagram, and many
   networks drop or filter fragments.
4. **Broadcast vs multicast; which is used and what would change?** Broadcast
   sends to every host on the subnet (no membership). Multicast sends to a group
   that hosts join; switches/routers can forward it selectively with IGMP. This
   chat uses broadcast (subnet + 255.255.255.255); multicast would require
   joining a group address/port and sending to that group.
5. **How many sockets? Bound to what?** Two per peer: the general socket on
   `0.0.0.0:<chatPort>` (shared, reuse-address/reuse-port, receives the general
   channel) and the private socket on `0.0.0.0:<ephemeral>` (unique, receives
   private messages).
6. **What does the UDP checksum protect against? Why not enough against a deliberate change?**
   It detects accidental corruption in transit (bit errors). It is not a
   cryptographic MAC: an attacker who changes the payload can recompute the
   checksum. AES-GCM's tag provides integrity/authenticity against anyone without
   the key.

## Cross-device notes (2026-11-26)

- A multi-homed Windows host (Wi-Fi + Hyper-V) must send the general-channel
  datagram to the directed broadcast of every interface (`192.168.1.255`), not
  only to `255.255.255.255`; the limited broadcast can leave through the wrong
  card. Both implementations now do this.
- A peer may advertise the wrong interface in `ep`. On receiving
  join/hello/heartbeat, keep the advertised private port but take the IP from
  the datagram source; private messages then work on the real LAN address.
- Verified with the Go peer on Android (192.168.1.5) and the C# peer on Windows
  (192.168.1.33): discovery, general channel, encrypted private messages both
  ways, leave. Logs:
  [evidence/cross-device-2026-11-26/](./evidence/cross-device-2026-11-26/README.md).
