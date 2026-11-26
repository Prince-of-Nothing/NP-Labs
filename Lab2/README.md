# Lab 2 — Coffee Shop Server (TCP)

Defense notes and control-question answers: [KNOWLEDGE.md](./KNOWLEDGE.md).

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
`order` / `deliver` with `items:[{"product":"coffee","qty":2}]` (orders also
carry a `tag` used to correlate metrics), `stats`, `quit`.

Server → client: `menu` (stock), `order_accepted` (id, prep ms, tag),
`order_refused` (product, available, tag), `order_ready` (id, tag), `alert`,
`delivery_ok`, `stats` (summary), `error`.

## Behaviour

- Stock starts at a random 10–50 per product.
- Bonus: the stock is saved to `stock-state.json` after every change and loaded
  again on restart, so a restart does not reset it.
- coffee 4 s, tea 3 s, croissant 2 s per unit.
- One worker per CPU core; FIFO queue; one whole order per worker.
- Stock is protected by a lock, so two clients cannot both take the last item.
- Low stock (<5) alerts the provider immediately, and on provider reconnect.
- Client disconnect cancels queued and in-progress orders; products return to stock.
- Invalid input produces an error and never crashes the server.

## Metrics for the report (automatic)

Run the C# server and client with the working directory `Lab2/csharp` so both
write into the same `metrics/` folder. Nothing has to be collected by hand.

- Server writes, after every finished order:
  - `metrics/server-orders.csv` — order id, endpoint, tag, items, expected prep,
    accepted/started/ready timestamps, queue wait, actual prep, worker id, status.
  - `metrics/server-summary.json` — refreshed every 2 s: connections, accepted/
    refused/ready/cancelled, deliveries, alerts, avg/max queue wait, avg/max
    prep, throughput, per-worker orders and utilisation.
- Client writes one set per client process, so many clients can run at once:
  - `metrics/client-orders-<pid>.csv` — tag, order id, items, accept latency,
    ready wait, total time, status.
  - `metrics/client-summary-<pid>.json` — sent/accepted/refused/ready, avg/max
    latencies, pending.
- Type `stats` in the client to print the live server summary and the client
  summary.
- Generate the report table and SVG charts (standard library only):

```powershell
python Lab2/scripts/metrics_report.py
# or: python Lab2/scripts/metrics_report.py --dir <metrics folder>
```

This writes `metrics/report.md`, `queue_wait.svg`, `prep_time.svg`,
`worker_orders.svg`, and `client_latency.svg`. It merges every
`client-orders-<pid>.csv`, so several clients can contribute to one report. The
generated files are ignored by Git; only the code and the script are committed.

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


## Cross-device evidence (2026-11-26)

Real run with the server on the Windows desktop and 16 Go clients + provider on
an Android phone (12 workers, 16 simultaneous clients, 16/16 ready, auth-free
raw TCP). See [Lab2/evidence/cross-device-2026-11-26/](./evidence/cross-device-2026-11-26/README.md).
The stock-persistence bonus is verified in
[Lab2/evidence/stock-persistence-2026-11-26/](./evidence/stock-persistence-2026-11-26/README.md).

