# Lab 2 — Coffee Shop Server (TCP)

Three roles over raw TCP sockets: **server**, **client**, **provider**. No HTTP,
no WebSocket, no RPC, no messaging library. In C# the `Socket` class is used
directly (no `TcpListener`, `TcpClient`, `NetworkStream`).

## Layout

- `csharp/Server` — coffee shop server.
- `csharp/Client` — interactive client; also runs as the provider (`--role provider`).
- `rust/server`, `rust/client` — Rust implementation.
- `go/server`, `go/client` — Go implementation.

## Build and run

```powershell
# C#
dotnet run --project csharp/Server/Server.csproj -- 5000
dotnet run --project csharp/Client/Client.csproj -- 127.0.0.1 5000 client
dotnet run --project csharp/Client/Client.csproj -- 127.0.0.1 5000 provider

# Rust
cd rust/server; cargo run -- 5000
cd rust/client; cargo run -- 127.0.0.1 5000 client

# Go
cd go/server; go run . 5000
cd go/client; go run . 127.0.0.1 5000 client
```

## Protocol (newline-delimited JSON)

Client/provider → server: `{"cmd":"hello","role":"client|provider"}`, `menu`,
`order` / `deliver` with `items:[{"product":"coffee","qty":2}]`, `quit`.

Server → client: `menu` (stock), `order_accepted` (id, prep ms),
`order_refused` (product, available), `order_ready`, `alert`, `delivery_ok`,
`error`.

## Behaviour

- Stock starts at a random 10–50 per product.
- coffee 4 s, tea 3 s, croissant 2 s per unit.
- One worker per CPU core; FIFO queue; one whole order per worker.
- Stock is protected by a lock, so two clients cannot both take the last item.
- Low stock (<5) alerts the provider immediately, and on provider reconnect.
- Client disconnect cancels queued and in-progress orders; products return to stock.
- Invalid input produces an error and never crashes the server.

## Docker

From the repository root:

```powershell
docker compose up -d coffee-server
docker compose run --rm coffee-client
docker compose run --rm coffee-provider
```

## Verified

- Last-item race: 1 accepted / 1 refused.
- Disconnect mid-order returns stock; invalid product rejected.
- Low-stock alert on threshold and on provider reconnect; provider disconnect
  does not stop the shop.
- 20 clients against 12 workers: all 20 orders accepted and all 20 became ready
  (FIFO queue, one worker per CPU core).
- Server disappearance: C#, Rust and Go clients exit cleanly (code 0).
