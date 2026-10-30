namespace Tasks;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Task 1c — Producer-consumer pipeline.
/// One reader thread reads lines and enqueues batches of 10 000 strings
/// into a BlockingCollection with capacity 100.
/// N worker threads dequeue batches and count signs.
/// </summary>
public class Task1C
{
    const int BatchSize   = 10_000;
    const int QueueCap    = 100;

    public static void Run(string[] args)
    {
        string input = GetStr(args, "--input", "numbers.txt");
        string csv   = GetStr(args, "--csv", "");
        int[] counts = ParseInts(GetStr(args, "--workers", ""));

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input not found: {input}");
            return;
        }

        if (counts.Length == 0)
            counts = new[] { 1, 2, 4, 8, 16, 32, 64 };

        Console.WriteLine($"File: {Path.GetFullPath(input)}");
        Console.WriteLine();

        bool firstCsv = !File.Exists(csv);
        var results = new List<(int workers, double ms, double peakMB, long total, bool ok)>();

        foreach (int n in counts)
        {
            var (r, ms, peakMB) = Measure(input, n);
            bool ok = r.Total > 0;
            results.Add((n, ms, peakMB, r.Total, ok));
            Console.WriteLine($"  workers={n,3}  time={ms,7:F0} ms  peak={peakMB,6:F1} MiB  " +
                              $"pos={r.Pos:N0} neg={r.Neg:N0} zero={r.Zero:N0}  total={r.Total:N0}");
        }

        // compute speedups (relative to first config)
        double baseMs = results[0].ms;

        if (csv.Length > 0)
        {
            using var sw = new StreamWriter(csv, append: !firstCsv);
            if (firstCsv) sw.WriteLine("task,threads,time_ms,speedup,peak_mb,pos,neg,zero,total,correct");
            foreach (var (w, ms, peak, total, ok) in results)
                sw.WriteLine($"1c,{w},{ms:F0},{baseMs / ms:F2},{peak:F2},{total},{ok}");
        }
    }

    // ── core pipeline ──────────────────────────────────────────────────
    static (Common.CounterResult, double, double) Measure(string path, int nWorkers)
    {
        var queue = new BlockingCollection<string[]>(QueueCap);
        var result = new Common.CounterResult();
        object resultSync = new();

        // peak-memory sampler
        using var proc = Process.GetCurrentProcess();
        long peak = 0;
        var sampler = new Thread(() =>
        {
            while (true)
            {
                proc.Refresh();
                long ws = proc.WorkingSet64;
                if (ws > peak) peak = ws;
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        sampler.Start();

        var sw = Stopwatch.StartNew();

        // producer
        var producer = new Thread(() =>
        {
            var batch = new List<string>(BatchSize);
            foreach (var line in File.ReadLines(path))
            {
                batch.Add(line);
                if (batch.Count >= BatchSize)
                {
                    queue.Add(batch.ToArray());
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                queue.Add(batch.ToArray());
            queue.CompleteAdding();
        }) { IsBackground = true };

        // consumers
        var workers = new Thread[nWorkers];
        for (int i = 0; i < nWorkers; i++)
        {
            workers[i] = new Thread(() =>
            {
                long lp = 0, ln = 0, lz = 0;
                foreach (var batch in queue.GetConsumingEnumerable())
                {
                    foreach (var line in batch)
                    {
                        if (line.Length == 0) continue;
                        if (line[0] == '-')
                        {
                            if (line.Length == 1) continue;
                            ln++;
                        }
                        else if (line[0] == '0' && line.Length == 1)
                            lz++;
                        else
                            lp++;
                    }
                }
                lock (resultSync)
                {
                    result.Pos  += lp;
                    result.Neg  += ln;
                    result.Zero += lz;
                }
            }) { IsBackground = true };
        }

        producer.Start();
        foreach (var w in workers) w.Start();
        producer.Join();
        foreach (var w in workers) w.Join();

        sw.Stop();
        sampler.Interrupt();

        return (result, sw.Elapsed.TotalMilliseconds, peak / (1024.0 * 1024.0));
    }

    // ── helpers ────────────────────────────────────────────────────────
    static string GetStr(string[] a, string k, string d)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == k) return a[i + 1];
        return d;
    }

    static int[] ParseInts(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<int>();
        return Array.ConvertAll(s.Split(',', StringSplitOptions.RemoveEmptyEntries),
                                x => int.Parse(x.Trim()));
    }
}
