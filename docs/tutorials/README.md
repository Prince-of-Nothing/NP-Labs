# NP-Labs tutorials

- [Launch tutorial](./01-launch.md) - prerequisites, one-time Windows setup,
  building everything into `dist/`, and starting Labs 1-4 (C#, Go, phone).
- [Testing tutorial](./02-testing.md) - the defense checklist: task-by-task
  steps, expected output, cross-device tests, and where the captured evidence
  lives.

## Screenshots

`./screenshots/` contains real console captures taken on the Windows test
machine (1536x864 desktop, i5-13420H, 12 logical processors) while the scripts
in the launch tutorial were running:

| File | What it shows |
|---|---|
| `lab2-server.png` | server: random stock, 12 workers, provider delivery, orders accepted/ready |
| `lab2-clients.png` | one provider + three concurrent clients, menu/order/ready |
| `lab3-alice.png` | peer Alice: `/users` with Bob, general message, encrypted private message |
| `lab3-bob.png` | peer Bob: join notices, `/users` with Alice, private message received |
| `lab4-http-broker.png` | C# HTTP broker: subscriber, 401/403/202, ids 1-3, deliveries |
| `lab4-http-subscriber.png` | subscriber receives the three messages in order |
| `lab4-http-publisher.png` | publisher gets `202 Accepted` with ids 1-3 |
| `lab4-http-auth.png` | no token -> 401, wrong topic -> 403, allowed -> 202 |
| `lab4-grpc-broker.png` | C# gRPC broker: subscriber, publish/deliver ids 1-3 |
| `lab4-grpc-subscriber.png` | gRPC subscriber receives the three messages |
| `lab4-grpc-publisher.png` | gRPC publisher gets `Published id=1..3` |

The phone screen cannot be captured from inside the Acode/Termux sandbox
(`screencap` is blocked by Android). Phone runs are proven by their datagram
logs, metrics and the desktop side of the cross-device evidence; the launch
tutorial gives the exact phone commands.
