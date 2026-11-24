# NP-Labs

Networking laboratory works (TCP/UDP/HTTP/gRPC). Each lab is implemented in C# (primary), with Lab 2 also in Rust and Go.

## Lab 2 - Coffee Shop Server (TCP)
- `lab2-coffee-shop/csharp`: Server + Client (client or provider role).
- `lab2-coffee-shop/rust`: server + client.
- `lab2-coffee-shop/go`: server + client.
- Run: `dotnet run --project Server/Server.csproj -- <port>` then `dotnet run --project Client/Client.csproj -- <host> <port> [client|provider]`.

## Lab 3 - LAN Chat (UDP)
- `lab3-lan-chat/csharp`: Chat peer. Run two+ instances: `dotnet run -- <name> <port> [passphrase]`.
- Uses broadcast for general channel, unique port for private messages, AES-GCM for secure private messages.

## Lab 4 - Message Broker (HTTP + gRPC)
- Part 1 HTTP: `lab4-message-broker/part1-http/{broker,publisher,subscriber}`.
- Part 2 gRPC: `lab4-message-broker/part2-grpc/{broker,publisher,subscriber}`.
- Broker: `dotnet run -- http://localhost:8080/` (HTTP) or `dotnet run` (gRPC, port 5000).
- Publisher: `dotnet run -- <brokerUrl> <topic> <sender> [token]`.
- Subscriber: `dotnet run -- <brokerUrl> <topic>`.

## Build
All C# projects target .NET 10. Build with `dotnet build` in each project folder.
Rust: `cargo build` in server/client. Go: `go build ./...` in server/client.

## Docker
`docker compose up --build` brings up all labs. Lab 3 chat uses host networking (Linux/Docker Desktop). Use `docker compose run --rm coffee-client` etc. for interactive.

## Windows Firewall (no more public/private prompts)
Run once as Administrator: `powershell -ExecutionPolicy Bypass -File enable-firewall.ps1`
Pre-allows all NP-Labs ports so Windows never asks.

## Verified behaviour (grade 10 tasks)
- Lab 2: menu/order/refuse/ready, workers per CPU, race on last item protected (1 accepted, 1 refused), client disconnect cancels order and returns stock, invalid input rejected.
- Lab 3: peers discover each other (simultaneous and staggered starts), general channel, private encrypted messages, join/leave, name uniqueness.
- Lab 4 HTTP: 202 publish, streamed delivery, topics list, restart keeps messages and ids, tokens (401/403/202).
- Lab 4 gRPC: unary publish, server-stream subscribe, topics, restart keeps ids, metadata auth (Unauthenticated/PermissionDenied/ok).