# Lab 2 - stock persistence (bonus), 2026-11-26

C# coffee-shop server on Windows, raw TCP port 5000. The stock is written to
`stock-state.json` (in the server's working directory) after every change and
loaded again at startup, so a restart keeps the remaining stock instead of
rolling a new random 10-50.

## Test sequence

1. fresh server -> random stock, written to `stock-state.json`;
2. client orders one coffee -> stock is deducted and saved;
3. server is stopped and started again -> stock is unchanged;
4. provider delivers 5 tea -> stock increases and is saved;
5. server is stopped and started again -> the delivery is still there.

| Phase | coffee | tea | croissant |
|---|---:|---:|---:|
| fresh | 21 | 19 | 21 |
| after `coffee:1` | 20 | 19 | 21 |
| after restart | 20 | 19 | 21 |
| after `deliver tea:5` | 20 | 24 | 21 |
| after second restart | 20 | 24 | 21 |

`summary.json` records all four checks as `true`:
`order_saved`, `restart_kept_order`, `delivery_saved`,
`restart_kept_delivery`.

## Implementation

- `PersistStock()` takes one snapshot under the stock lock, then writes a
  temporary file and renames it over `stock-state.json` (atomic replace).
  Writers are serialised by a separate file lock, so an older snapshot can
  never overwrite a newer one.
- Saved after an accepted order, a delivery, and a cancellation refund.
- `TryLoadStock()` validates that all three products exist with sane
  non-negative values; a missing/corrupt file falls back to a random stock.
- The state file is runtime data and is ignored by Git.

## Files

| File | What it is |
|---|---|
| `server-1.log` | fresh start (random stock) + order |
| `server-2.log` | restart: `Stock loaded from stock-state.json: coffee=20, tea=19, croissant=21` + delivery |
| `server-3.log` | restart after delivery: `coffee=20, tea=24, croissant=21` |
| `client-order.txt` | client: menu, order coffee:1, ready |
| `client-menu-after-restart.txt` | menu after restart shows coffee=20 |
| `provider-delivery.txt` | provider delivers tea:5 |
| `summary.json` | machine-readable before/after snapshots and checks |
| `stock-state-final.json` | the final persisted state file |
| `stock-test-192.168.1.33.txt` | full test transcript |

