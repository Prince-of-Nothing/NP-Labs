# Lab 2 - stock persistence: Go server, 2026-11-26

The Go coffee-shop server (`Lab2/go/server`) was tested with the same sequence
as the C# server, with the C# client (cross-language). Chat-free raw TCP on
port 5000, Windows desktop.

## Sequence

1. fresh server -> random stock, written to `stock-state.json`;
2. client orders one coffee -> stock is deducted and saved;
3. restart -> stock is unchanged;
4. provider delivers 5 tea -> stock increases and is saved;
5. restart -> the delivery is still there.

| Phase | coffee | tea | croissant |
|---|---:|---:|---:|
| fresh | 24 | 46 | 30 |
| after `coffee:1` | 23 | 46 | 30 |
| after restart | 23 | 46 | 30 |
| after `deliver tea:5` | 23 | 51 | 30 |
| after second restart | 23 | 51 | 30 |

All four checks in `summary.json` are `true` (`order_saved`,
`restart_kept_order`, `delivery_saved`, `restart_kept_delivery`).

## Files

| File | What it is |
|---|---|
| `server-1.log` | fresh start (random stock) + order |
| `server-2.log` | restart: `Stock loaded from stock-state.json: coffee=23, tea=46, croissant=30` + delivery |
| `server-3.log` | restart after delivery: `coffee=23, tea=51, croissant=30` |
| `client-order.txt` | C# client: menu, order coffee:1, ready |
| `client-menu-after-restart.txt` | menu after restart shows coffee=23 |
| `provider-delivery.txt` | provider delivers tea:5 |
| `summary.json` | snapshots and checks |
| `stock-state-final.json` | final persisted state |
| `stock-test-go-192.168.1.33.txt` | full test transcript |

## Implementation

`saveStock()` in `Lab2/go/server/main.go` snapshots the stock under the stock
mutex, writes `stock-state.json.tmp`, then renames it over `stock-state.json`;
a separate file mutex serialises writers. `loadStock()` validates the three
products and sane values; a missing/corrupt file falls back to a random stock.
The state is saved after an accepted order, a delivery and a cancellation
refund, and reloaded at startup.
