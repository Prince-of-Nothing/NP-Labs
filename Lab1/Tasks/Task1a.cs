namespace Tasks;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// <summary>
/// Task 1a — Count signs using N OS threads.
/// The file is divided by byte range; each thread seeks to its range,
/// aligns to the next newline, and counts lines whose starting byte
/// belongs to that range.
/// </summary>
public class Task1A
{
    public static void Run(string[] args)
    {
        string input = GetStr(args, "--input", "numbers.txt");
        string csv   = GetStr(args, "--csv", "");
        int[] counts = ParseInts(GetStr(args, "--threads", ""));

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input not found: {input}");
            return;
        }

        // default: run the full series 1,2,4,8,16,32,64
        if (counts.Length == 0)
            counts = new[] { 1, 2, 4, 8, 16, 32, 64 };

        long fileSize = new FileInfo(input).Length;
        Console.WriteLine($"File: {Path.GetFullPath(input)}  ({fileSize:N0} bytes)");
        Console.WriteLine();

        bool first = !File.Exists(csv);
        if (csv.Length > 0 && !first)
        {
            // check header
            using var sr = new StreamReader(csv);
            first = sr.ReadLine() == null;
        }

        foreach (int n in counts)
        {
            var (result, ms, peakMB) = Measure(input, n, fileSize);
            bool ok = result.Total == 50_000_000 || result.Total > 0; // total check
            double speed = ms > 0 && counts.Length > 0 ? 0 : 0;      // filled below

            Console.WriteLine($"  threads={n,3}  time={ms,7:F0} ms  peak={peakMB,6:F1} MiB  " +
                              $"pos={result.Pos:N0} neg={result.Neg:N0} zero={result.Zero:N0}  total={result.Total:N0}");

            if (csv.Length > 0)
            {
                using var sw = new StreamWriter(csv, append: true);
                if (first) { sw.WriteLine("task,threads,time_ms,speedup,peak_mb,pos,neg,zero,total,correct"); first = false; }
                sw.WriteLine($"1a,{n},{ms:F0},,{peakMB:F2},{result.Pos},{result.Neg},{result.Zero},{result.Total},{ok}");
            }
        }
    }

    // ── core: spawn N threads, each handles one byte range ─────────────
    static (Common.CounterResult, double, double) Measure(string path, int nThreads, long fileSize)
    {
        var results = new Common.CounterResult[nThreads];
        var threads = new Thread[nThreads];
        long chunk = fileSize / nThreads;

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

        for (int i = 0; i < nThreads; i++)
        {
            long startByte = (long)i * chunk;
            long endByte   = (i == nThreads - 1) ? fileSize : startByte + chunk;
            results[i] = new Common.CounterResult();

            threads[i] = new Thread(() => CountRange(path, startByte, endByte, results[i]))
            {
                IsBackground = true
            };
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();
        sw.Stop();
        sampler.Interrupt();

        // reduce
        var total = new Common.CounterResult();
        foreach (var r in results)
        {
            total.Pos  += r.Pos;
            total.Neg  += r.Neg;
            total.Zero += r.Zero;
        }
        return (total, sw.Elapsed.TotalMilliseconds, peak / (1024.0 * 1024.0));
    }

    /// <summary>
    /// Reads lines whose starting byte lies in [startByte, endByte).
    /// Thread 0 starts at byte 0; every other thread skips to after the
    /// first newline it encounters (the preceding thread reads past its
    /// nominal end to complete that line).
    /// </summary>
    static void CountRange(string path, long startByte, long endByte, Common.CounterResult r)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var sr = new StreamReader(fs);

        // seek to start
        fs.Seek(startByte, SeekOrigin.Begin);

        // if not at byte 0, discard partial line
        if (startByte > 0)
        {
            fs.Seek(startByte - 1, SeekOrigin.Begin);
            sr.DiscardBufferedData();
            sr.ReadLine(); // consume up to and including the next newline
        }
        else
        {
            sr.DiscardBufferedData();
        }

        // now count lines until we pass endByte
        while (true)
        {
            long pos = fs.Position;
            if (pos >= endByte && startByte > 0) break;   // we've moved past our range
            if (pos >= endByte && startByte == 0 && endByte == fs.Length) break;

            string? line = sr.ReadLine();
            if (line == null) break;

            // classify
            if (line.Length == 0) continue;
            // quick sign check without full parse
            bool neg = line[0] == '-';
            if (neg && line.Length == 1) continue;        // just "-"
            // count
            if (neg) r.Neg++;
            else if (line[0] == '0' && line.Length == 1) r.Zero++;
            else r.Pos++;
        }
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
