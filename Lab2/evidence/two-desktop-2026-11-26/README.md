# Lab 2 - two-desktop run (2026-11-26)

Server: **C# / .NET 10 on the Windows desktop** (`DESKTOP-8MKK1R0`,
192.168.1.41), 12 logical CPUs -> 12 workers. Clients and provider:
**Go 1.27.4 on the CachyOS desktop** (`Cupcake`, 192.168.1.22). Raw TCP port
5000, newline-delimited JSON. This is the two-computers + two-languages bonus
with two real desktop computers.

## Main run

- Provider delivered `coffee:40 tea:40 croissant:40`.
- **16 Go clients** connected at the same time (more than the 12 workers).
- Results: **16 accepted, 16 ready, 0 refused, 0 cancelled, 17 connections**.
- Queue wait: avg **1048.5 ms**, max **4194.4 ms** (4 clients queued behind
  the 12 busy workers).
- Preparation: avg **4213.9 ms**, max **4241.5 ms**.
- Throughput: **0.44 orders/s**.
- Four workers prepared two orders each; the other eight prepared one order.

## Second run: five C# clients on the Windows desktop

Five C# clients ran at the same time against the same server. With 12 workers
there was no queueing:

- 5 accepted / 5 ready / 0 refused / 0 cancelled.
- Avg accept latency: **9.0 ms**.
- Avg ready wait: **4197.6 ms**.
- Avg total: **4206.4 ms**.

## Third run: disconnect and refund

The first Go run let the clients quit 8 s after ordering. The 12 first orders
were ready; the 4 queued orders were cancelled when their clients left, and the
server returned their products to the stock. This exercises Task 4 with real
cross-device clients:

- 16 accepted / 12 ready / **4 cancelled**.
- Server log: `Order 13 cancelled (client gone); products returned` ...
  `Order 16 cancelled (client gone); products returned`.

The last-item race and low-stock alerts are unchanged and remain documented in
`Lab2/README.md`; this folder focuses on the two-desktop concurrency,
client-side latency and disconnect-refund evidence.

## Files

| File | What it is |
|---|---|
| `linux-go-clients/` | transcripts of the 16 Go clients + provider on Cupcake (main run) |
| `linux-go-clients-cancel/` | transcripts of the 16 Go clients + provider that quit after 8 s (4 cancellations) |
| `server-desktop.txt` | C# server console log of the main run |
| `server-cancel-run.txt` | C# server console log of the disconnect run |
| `server-metrics/` | **16-client** `server-orders.csv`, `server-summary.json`, generated `report.md` and SVG charts |
| `server-metrics-cancel/` | server CSV/JSON of the disconnect run |
| `server-metrics-combined/` | server CSV/JSON of the main run plus the 5 C# clients (21 orders) |
| `windows-csharp-clients/` | transcripts of the five C# clients |
| `windows-csharp-clients-metrics/` | five `client-orders-<pid>.csv` + summaries, filtered server CSV, `report.md`, `client_latency.svg` |

## Reproduce

```powershell
# desktop A, C# server (working directory Lab2/csharp)
Remove-Item stock-state.json -ErrorAction SilentlyContinue
dotnet run --project Server/Server.csproj -- 5000
```

```bash
# desktop B, Go client / provider (built with Go)
./lab2-client 192.168.1.41 5000 provider
./lab2-client 192.168.1.41 5000 client
```

The Go clients used a timed stdin pipe:

```bash
( sleep 1; echo "deliver coffee:40 tea:40 croissant:40"; sleep 3; echo quit ) \
  | ./lab2-client 192.168.1.41 5000 provider

( sleep 1; echo "order coffee:1"; sleep 16; echo quit ) \
  | ./lab2-client 192.168.1.41 5000 client
```

Regenerate the tables and charts:

```powershell
python Lab2/scripts/metrics_report.py --dir Lab2/evidence/two-desktop-2026-11-26/server-metrics
python Lab2/scripts/metrics_report.py --dir Lab2/evidence/two-desktop-2026-11-26/windows-csharp-clients-metrics
```
