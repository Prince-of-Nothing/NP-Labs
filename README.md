# NP-Labs — Networking Laboratory Works

## Lab 1 — Concurrency in Practice (C#/.NET)
The C#/.NET 8 implementation for Concurrency in Practice is in [Lab1/](./Lab1/).
See [Lab1/README.md](./Lab1/README.md) for build and run instructions for data
generation, Tasks 1a/1b/1c, and Task 2.

The `Lab1/usb/` folder is a local staging area for the eight files used in Task
2 and is intentionally ignored by Git.

## Lab 2 — Coffee Shop Server (TCP)
See [Lab2/README.md](./Lab2/README.md).
- `Lab2/csharp`: Server + Client (client or provider role).
- `Lab2/rust`: server + client.
- `Lab2/go`: server + client.
- Run: `dotnet run --project Server/Server.csproj -- <port>` then `dotnet run --project Client/Client.csproj -- <host> <port> [client|provider]`.

## Lab 3 — LAN Chat (UDP)
See [Lab3/README.md](./Lab3/README.md).
- `Lab3/csharp`: Chat peer. Run two+ instances: `dotnet run -- <name> <port> [passphrase]`.
- `Lab3/go`: Go chat peer (stdlib only): `go run . <name> <port> [passphrase]`.
- Uses broadcast for general channel, unique port for private messages, AES-GCM for secure private messages.

## Lab 4 — Message Broker (HTTP + gRPC)
See [Lab4/README.md](./Lab4/README.md).
- Part 1 HTTP: `Lab4/part1-http/{broker,publisher,subscriber}`.
- Part 2 gRPC: `Lab4/part2-grpc/{broker,publisher,subscriber}`.
- Go: `Lab4/part1-http/go` and `Lab4/part2-grpc/go`.
- Broker: `dotnet run -- http://localhost:8080/` (HTTP) or `dotnet run` (gRPC, port 5000).
- Publisher: `dotnet run -- <brokerUrl> <topic> <sender> [token]`.
- Subscriber: `dotnet run -- <brokerUrl> <topic>`.

## Build
All C# projects target .NET 10 (Lab 1 targets .NET 8). Build with `dotnet build` in each project folder.
Rust: `cargo build` in server/client. Go: `go build ./...` in server/client.

## Docker
`docker compose up --build` brings up labs 2–4. Lab 3 chat uses host networking (Linux/Docker Desktop). Use `docker compose run --rm coffee-client` etc. for interactive.

## Windows Firewall (no more public/private prompts)
Run once as Administrator: `powershell -ExecutionPolicy Bypass -File enable-firewall.ps1`
Pre-allows all NP-Labs ports so Windows never asks.

## Verified behaviour (grade 10 tasks)
- Lab 2: menu/order/refuse/ready, workers per CPU, race on last item protected (1 accepted, 1 refused), client disconnect cancels order and returns stock, invalid input rejected, low-stock alerts immediate and on provider reconnect.
- Lab 3: peers discover each other (simultaneous and staggered starts), general channel, private encrypted messages, join/leave, `/users`, name uniqueness, invalid datagrams dropped and logged, 500-char limit.
- Lab 4 HTTP: 202 publish, streamed delivery, multiple publishers (ids 1..N unique) and multiple subscribers, topics list, restart keeps messages and ids, tokens (401/403/202).
- Lab 4 gRPC: unary publish, server-stream subscribe, topics, restart keeps ids, metadata auth (Unauthenticated/PermissionDenied/ok).
- Go ports: Lab 2 (server/client), Lab 3 (chat), Lab 4 HTTP and gRPC all build; Go gRPC client works against the C# broker (cross-language bonus).

## Swiss army knife scripts (Lab 1)
`scripts/` contains two equivalent entry points that drive the whole lab:
prerequisite check, build, data generation, every task, hardware capture,
result verification, and report charts.

- Windows: `powershell -ExecutionPolicy Bypass -File scripts\lab.ps1 <command>`
- Linux (CachyOS): `scripts/lab.sh <command>` (run `chmod +x scripts/lab.sh` once)

Both print identical help for the same verbs (`check`, `status`, `build`,
`generate`, `split`, `task1a`, `task1b`, `task1c`, `task1`, `task2`,
`hardware`, `verify`, `charts`, `all`, `clean`). A typical session:

```text
lab check                          verify .NET 8 SDK, git, python + matplotlib
lab all                            build, generate 50M numbers, split into 8
                                   files, run tasks 1a/1b/1c, capture hardware,
                                   verify the totals, and draw the charts
lab task2 --dir <usb path>         Task 2 with an eject/replug pause per
                                   configuration, as the lab requires
lab verify                         cross-check that every correct row agrees
lab charts                         regenerate Lab1/charts/*.png for the report
```

Generated artifacts (`numbers.txt`, `usb/`, `charts/`, `hardware.txt`,
`results.csv`) are ignored by Git.