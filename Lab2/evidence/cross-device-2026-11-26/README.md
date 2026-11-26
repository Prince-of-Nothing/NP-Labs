# Lab 2 - cross-device run (2026-11-26)

Server: **C# / .NET 10 on the Windows desktop** (192.168.1.33), 12 logical CPUs
-> 12 workers. Clients and provider: **Go (arm64) on the Android phone**
(192.168.1.5). Raw TCP port 5000, newline-delimited JSON. This is the
two-computers + two-languages bonus.

## Run

- Provider delivered `coffee:40 tea:40 croissant:40`.
- **16 clients** connected at the same time (more than the 12 workers).
- Results: **16 accepted, 16 ready, 0 refused, 0 cancelled, 17 connections**.
- Queue wait: avg **1229.8 ms**, max **6563.9 ms** (4 clients queued behind
  the 12 busy workers).
- Preparation: avg **5748.3 ms**, max **6592.8 ms**.
- Every order was accepted once and became ready exactly once; the per-worker
  distribution and utilisation are in `server-metrics/server-summary.json`.

## Files

| File | What it is |
|---|---|
| `phone-clients-192.168.1.5.txt` | transcripts of the 16 Go clients + provider on the phone |
| `server-desktop-192.168.1.33.txt` | server console log (connections, orders, ready, deliveries) |
| `server-metrics/server-orders.csv` | one row per finished order (queue wait, prep, worker, status) |
| `server-metrics/server-summary.json` | counters, averages and per-worker utilisation |
| `server-metrics/report.md` | generated report |
| `server-metrics/queue_wait.svg`, `prep_time.svg`, `worker_orders.svg` | charts |
| `desktop-csharp-clients-metrics/*` | **5 desktop C# clients at the same time**: each writes `client-orders-<pid>.csv` + `client-summary-<pid>.json`, `report.md` merges all five, and `desktop-csharp-clients-192.168.1.33.txt` is their transcript (the Go client writes no CSVs) |

## Reproduce

```powershell
# desktop
dotnet run --project Lab2/csharp/Server/Server.csproj -- 5000
# phone (arm64 cross-build)
./bin/arm64/lab2-client 192.168.1.33 5000 client
# provider (another phone/desktop Go client)
./bin/arm64/lab2-client 192.168.1.33 5000 provider
```
