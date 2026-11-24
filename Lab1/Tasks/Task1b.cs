namespace Tasks;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// <summary>
/// Task 1b — Three ways to count signs from an in-memory int array.
/// Version 1: shared counters, no synchronization (run 3×).
/// Version 2: shared counters protected by lock.
/// Version 3: per-thread local counters, merged at the end.
/// </summary>
public class Task1B
{
    public static void Run(string[] args)
    {
        string input = GetStr(args, "--input", "numbers.txt");
        string csv   = GetStr(args, "--csv", "");
        int threads  = int.TryParse(GetStr(args, "--threads", ""), out int t) ? t : Environment.ProcessorCount;

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input not found: {input}");
            return;
        }

        Console.WriteLine($"Loading array from {input} …");
        var sw0 = Stopwatch.StartNew();
        int[] data = LoadArray(input);
        sw0.Stop();
        Console.WriteLine($"  loaded {data.Length:N0} ints in {sw0.Elapsed.TotalMilliseconds:F0} ms (not measured)");
        Console.WriteLine($"  threads = {threads} (Environment.ProcessorCount)");
        Console.WriteLine();

        bool firstCsv = !File.Exists(csv);
        var rows = new List<(string label, double ms, long total, bool correct)>();

        // ── Version 1: unsynchronized × 3 ──────────────────────────
        for (int run = 1; run <= 3; run++)
        {
            long pos = 0, neg = 0, zero = 0;
            var sw = Stopwatch.StartNew();
            RunUnsync(data, threads, ref pos, ref neg, ref zero);
            sw.Stop();
            long total = pos + neg + zero;
            bool ok = total == data.Length;
            Console.WriteLine($"  Unsynchronized run {run}: {sw.Elapsed.TotalMilliseconds,7:F0} ms  total={total:N0}  {(ok ? "OK" : $"LOST {data.Length - total:N0}")}");
            rows.Add(($"unsync-run{run}", sw.Elapsed.TotalMilliseconds, total, ok));
        }

        // ── Version 2: lock ────────────────────────────────────────
        {
            long pos = 0, neg = 0, zero = 0;
            var sw = Stopwatch.StartNew();
            RunLock(data, threads, ref pos, ref neg, ref zero);
            sw.Stop();
            long total = pos + neg + zero;
            bool ok = total == data.Length;
            Console.WriteLine($"  Lock-protected:        {sw.Elapsed.TotalMilliseconds,7:F0} ms  total={total:N0}  {(ok ? "OK" : "WRONG")}");
            rows.Add(("lock", sw.Elapsed.TotalMilliseconds, total, ok));
        }

        // ── Version 3: thread-local ────────────────────────────────
        {
            var sw = Stopwatch.StartNew();
            var (pos, neg, zero) = RunLocal(data, threads);
            sw.Stop();
            long total = pos + neg + zero;
            bool ok = total == data.Length;
            Console.WriteLine($"  Thread-local:          {sw.Elapsed.TotalMilliseconds,7:F0} ms  total={total:N0}  {(ok ? "OK" : "WRONG")}");
            rows.Add(("local", sw.Elapsed.TotalMilliseconds, total, ok));
        }

        // CSV
        if (csv.Length > 0)
        {
            using var sw = new StreamWriter(csv, append: !firstCsv);
            if (firstCsv) sw.WriteLine("task,threads,time_ms,speedup,peak_mb,pos,neg,zero,total,correct");
            foreach (var (label, ms, total, ok) in rows)
                sw.WriteLine($"1b-{label},{threads},{ms:F0},,,{total},{ok}");
        }
    }

    // ── implementation ─────────────────────────────────────────────────

    static void RunUnsync(int[] data, int n, ref long pos, ref long neg, ref long zero)
    {
        long lp = 0, ln = 0, lz = 0;  // local copies
        int per = data.Length / n;
        var threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                int start = idx * per;
                int end   = (idx == n - 1) ? data.Length : start + per;
                for (int j = start; j < end; j++)
                {
                    int v = data[j];
                    if (v > 0) lp++;        // RACE: unsynchronized!
                    else if (v < 0) ln++;
                    else lz++;
                }
            }) { IsBackground = true };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();
        pos = lp; neg = ln; zero = lz;
    }

    static void RunLock(int[] data, int n, ref long pos, ref long neg, ref long zero)
    {
        long lp = 0, ln = 0, lz = 0;
        object sync = new();
        int per = data.Length / n;
        var threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                int start = idx * per;
                int end   = (idx == n - 1) ? data.Length : start + per;
                for (int j = start; j < end; j++)
                {
                    int v = data[j];
                    lock (sync)
                    {
                        if (v > 0) lp++;
                        else if (v < 0) ln++;
                        else lz++;
                    }
                }
            }) { IsBackground = true };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();
        pos = lp; neg = ln; zero = lz;
    }

    static (long, long, long) RunLocal(int[] data, int n)
    {
        var localPos = new long[n];
        var localNeg = new long[n];
        var localZero = new long[n];
        int per = data.Length / n;
        var threads = new Thread[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                long lp = 0, ln = 0, lz = 0;
                int start = idx * per;
                int end   = (idx == n - 1) ? data.Length : start + per;
                for (int j = start; j < end; j++)
                {
                    int v = data[j];
                    if (v > 0) lp++;
                    else if (v < 0) ln++;
                    else lz++;
                }
                localPos[idx]  = lp;
                localNeg[idx]  = ln;
                localZero[idx] = lz;
            }) { IsBackground = true };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();

        long pos = 0, neg = 0, zero = 0;
        for (int i = 0; i < n; i++) { pos += localPos[i]; neg += localNeg[i]; zero += localZero[i]; }
        return (pos, neg, zero);
    }

    static int[] LoadArray(string path)
    {
        var lines = File.ReadLines(path);
        // first pass: count
        long count = 0;
        foreach (var _ in lines) count++;
        // second pass: parse
        var arr = new int[count];
        long i2 = 0;
        foreach (var line in File.ReadLines(path))
            arr[i2++] = int.Parse(line);
        return arr;
    }

    static string GetStr(string[] a, string k, string d)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == k) return a[i + 1];
        return d;
    }
}
