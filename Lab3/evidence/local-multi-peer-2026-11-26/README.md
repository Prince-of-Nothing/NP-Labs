# Lab 3 - three peers on one phone (same port), 2026-11-26

All three Go peers run on the Android phone, same chat port 9100,
shared with SO_REUSEPORT. Passphrase: `secret`.

## Alice
```text
* Alice joined (private ep 192.168.1.5:42660). Type messages, /msg name text, /users, /quit
* Bob joined the chat
* Cara joined the chat
Online:
  Bob 192.168.1.5:40329
  Cara 192.168.1.5:43063
[general] Alice: general from Alice
[private] you -> Bob: private Alice to Bob
[general] Cara: general from Cara
```

## Bob
```text
* Alice joined the chat
* Cara joined the chat
* Bob joined (private ep 192.168.1.5:40329). Type messages, /msg name text, /users, /quit
Online:
  Alice 192.168.1.5:42660
  Cara 192.168.1.5:43063
[general] Alice: general from Alice
[private] Alice: private Alice to Bob
[general] Cara: general from Cara
* Alice left the chat
```

## Cara
```text
* Bob joined the chat
* Alice joined the chat
* Cara joined (private ep 192.168.1.5:43063). Type messages, /msg name text, /users, /quit
Online:
  Bob 192.168.1.5:40329
  Alice 192.168.1.5:42660
[general] Alice: general from Alice
[general] Cara: general from Cara
* Alice left the chat
* Bob left the chat
```

## Checks

- bob_sees_alice_general: **true**
- bob_sees_cara_general: **true**
- cara_sees_alice_general: **true**
- bob_receives_private: **true**
- cara_does_not_receive_private: **true**
- bob_sees_alice_left: **true**
- cara_sees_alice_left: **true**
- alice_users_lists_all: **true**

**All checks passed.**

Datagram logs: the `chat-<name>-9100.txt` files (renamed from `.log`)
contain every SEND/RECV with endpoint, kind and size; Bob's log shows the
private datagram on his unique private port, Cara's shows only the two
broadcasts.
