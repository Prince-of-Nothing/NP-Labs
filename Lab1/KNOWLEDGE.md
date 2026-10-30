# Lab1 Knowledge Source — Concurrency Measurements

## Hardware (verified)
Intel Core i5-13420H (13th Gen), 8 cores / 8 logical, 3.24 GHz, 15.7 GB, Samsung NVMe, Kingston USB 3.0 (F:/VENTOY FAT32), Win 10 LTSC 19044, .NET 8.0.425.

## Task 1a — Thread Scaling (50 M integers, verified 50,000,000 total)
| p | ms | Speed-up | Peak MB |
|---|----|----------|---------|
1  936  1.00x  34.7
2  505  1.85x  35.3
4  280  3.34x  36.7
8  284  3.29x  37.8
16 253  3.71x  40.2
32 243  3.85x  45.3
64 238  3.93x  57.7

## Task 1b — Synchronization (8 threads)
Unsync run 1: 202 ms, total ~10,631,994 (lost ~79% updates)
Lock-protected: 3,979 ms, total 50,000,000 (correct, slow)
Thread-local: 47 ms, total 50,000,000 (correct, fastest)

## Task 1c — Producer-Consumer (bounded queue 100 batches / 10k lines)
1w 1061 | 2w 1197 | 4w 1607 | 8w 2269 | 16w 2346 | 32w 3308 | 64w 3383
All correct totals = 50,000,000.

## Task 2 — USB Cold-Plug (F:\lab1, no OS cache, 3 runs)
1t: 5359 ms | 2t: 4670 ms | 4t: 4571 ms — all correct totals 50,000,000.
Throughput ~78 MB/s (419 MB / 5359 ms) vs NVMe ~448 MB/s (5.7x slower).

## Charts
- charts/task1a.png (execution time + speed-up)
- charts/task1b.png (sync strategies)
- charts/task1c.png (producer-consumer scaling)

## Source / Build
Repo: https://github.com/Prince-of-Nothing/CS-labs.git (Lab1/)
Source: Lab1/Program.cs, Tasks/*.cs, Common/*.cs, Lab1.csproj
Report: Lab1/report/report.tex → report/report.pdf (tectonic compiled)
