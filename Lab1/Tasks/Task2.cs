namespace Tasks;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

/// <summary>
/// Task 2 — Read multiple files from slow storage (USB drive).
/// Each file is a unit of work: one thread reads it in a single operation
/// into one byte buffer, parses it, and counts its numbers.
/// Files are divided among threads as evenly as possible (by size).
/// </summary>
public class Task2
{
    public static void Run(string[] args)
    {
        string dir     = GetStr(args, "--dir", "usb");
        string csv     = GetStr(args, "--csv", "");
        int[] counts   = ParseInts(GetStr(args, "--threads", ""));

        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Directory not found: {dir}");
            return;
        }

        // gather the 8 files
        var files = Directory.GetFiles(dir, "numbers-*.txt")
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .ToArray();

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No numbers-*.txt files in {dir}");
            return;
        }

        Console.WriteLine($"Directory: {Path.GetFullPath(dir)}");
        Console.WriteLine($"Files: {files.Length}");
        foreach (var f in files)
            Console.WriteLine($"  {Path.GetFileName(f)}  ({new FileInfo(f).Length:N0} bytes)");
        Console.WriteLine();

        if (counts.Length == 0)
            counts = new[] { 1, 2, 4 };

        bool firstCsv = !File.Exists(csv);
        var results = new List<(int threads, double ms, double peakMB, long total, bool ok)>();

        foreach (int n in counts)
        {
            var (r, ms, peakMB) = Measure(files, n);
            bool ok = r.Total > 0;
            results.Add((n, ms, peakMB, r.Total, ok));
            Console.WriteLine($"  threads={n}  time={ms,7:F0} ms  peak={peakMB,6:F1} MiB  " +
                              $"pos={r.Pos:N0} neg={r.Neg:N0} zero={r.Zero:N0}  total={r.Total:N0}");
        }

        double baseMs = results[0].ms;

        if (csv.Length > 0)
        {
            using var sw = new StreamWriter(csv, append: !firstCsv);
            if (firstCsv) sw.WriteLine("task,threads,time_ms,speedup,peak_mb,pos,neg,zero,total,correct");
            foreach (var (th, ms, peak, total, ok) in results)
                sw.WriteLine($"2,{th},{ms:F0},{baseMs / ms:F2},{peak:F2},{total},{ok}");
        }
    }

    // ── core ───────────────────────────────────────────────────────────
    static (Common.CounterResult, double, double) Measure(string[] files, int nThreads)
    {
        // size-balanced assignment: sort files by size desc, greedy bin-pack
        var sorted = files.OrderByDescending(f => new FileInfo(f).Length).ToArray();
        var bins = new (long size, List<string> files)[nThreads];
        for (int i = 0; i < nThreads; i++) bins[i] = (0, new List<string>());

        foreach (var f in sorted)
        {
            long sz = new FileInfo(f).Length;
            int minBin = 0;
            long minSize = long.MaxValue;
            for (int b = 0; b < nThreads; b++)
                if (bins[b].size < minSize) { minSize = bins[b].size; minBin = b; }
            bins[minBin].size += sz;
            bins[minBin].files.Add(f);
        }

        var results = new Common.CounterResult[nThreads];
        var threads = new Thread[nThreads];

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
            var myFiles = bins[i].files.ToArray();
            results[i] = new Common.CounterResult();
            threads[i] = new Thread(() =>
            {
                var r = results[i];
                foreach (var path in myFiles)
                {
                    // single read into one byte buffer (as required)
                    byte[] buf = File.ReadAllBytes(path);
                    ParseBuffer(buf, r);
                }
            }) { IsBackground = true };
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();
        sw.Stop();
        sampler.Interrupt();

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
    /// Parses a byte buffer of newline-separated integers and counts signs.
    /// Handles optional leading '-' and multi-digit values.
    /// </summary>
    static void ParseBuffer(byte[] buf, Common.CounterResult r)
    {
        int len = buf.Length;
        int i = 0;
        while (i < len)
        {
            // skip to first digit or minus
            byte b = buf[i];
            if (b == '\n' || b == '\r' || b == ' ') { i++; continue; }

            bool neg = false;
            if (b == '-') { neg = true; i++; }

            // read digits
            int start = i;
            while (i < len && buf[i] >= '0' && buf[i] <= '9') i++;
            int digitLen = i - start;

            if (digitLen == 0) continue;

            if (neg) r.Neg++;
            else if (digitLen == 1 && buf[start] == '0') r.Zero++;
            else r.Pos++;

            // skip newline
            while (i < len && (buf[i] == '\n' || buf[i] == '\r')) i++;
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
