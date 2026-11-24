using System;
using Common;

internal class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            switch (args[0])
            {
                case "generate": Generate(args); break;
                case "split":    Split(args);    break;
                case "1a":       Tasks.Task1A.Run(args); break;
                case "1b":       Tasks.Task1B.Run(args); break;
                case "1c":       Tasks.Task1C.Run(args); break;
                case "2":        Tasks.Task2.Run(args);  break;
                case "hardware": PrintHardware(); break;
                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    PrintUsage();
                    return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            Lab1 — Concurrency in Practice (Network Programming)

            Usage: dotnet run -c Release -- <command> [options]

            Commands:
              generate   [--count N] [--seed S] [--out PATH]
                         Generate N random integers in [-1000000, 1000000].
                         Default: 50 000 000 numbers → numbers.txt

              split      [--count N] [--out DIR]
                         Split numbers.txt into 8 ordered files of equal
                         line count inside DIR (default: usb/).

              1a         [--threads N] [--input PATH] [--csv PATH]
                         Count signs with N OS threads (byte-range split).

              1b         [--threads N] [--input PATH] [--csv PATH]
                         Three strategies: unsync ×3, lock, thread-local.

              1c         [--workers N] [--input PATH] [--csv PATH]
                         Producer–consumer with bounded batch queue.

              2          [--dir PATH] [--threads N] [--csv PATH]
                         Multi-file processing on USB (slow storage).

              hardware   Print OS / runtime / core information.

            Examples:
              dotnet run -c Release -- generate
              dotnet run -c Release -- split
              dotnet run -c Release -- 1a --threads 4
              dotnet run -c Release -- 2 --dir F:\\ --threads 1
            """);
    }

    static void Generate(string[] args)
    {
        long count = GetLong(args, "--count", 50_000_000);
        int  seed  = GetInt (args, "--seed", 0);
        string outPath = GetString(args, "--out", "numbers.txt");
        DataGenerator.Generate(outPath, count, seed);
    }

    static void Split(string[] args)
    {
        long count  = GetLong(args, "--count", 50_000_000);
        string src  = GetString(args, "--input", "numbers.txt");
        string dir  = GetString(args, "--out", "usb");
        DataGenerator.Split(src, dir, parts: 8);
    }

    static void PrintHardware()
    {
        Console.WriteLine($"OS:              {Environment.OSVersion}");
        Console.WriteLine($"Runtime:         {Environment.Version}");
        Console.WriteLine($"Logical CPUs:    {Environment.ProcessorCount}");
        Console.WriteLine($"64-bit:          {Environment.Is64BitOperatingSystem}");
        long gcm = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        Console.WriteLine($"Available RAM:   {gcm} MiB");
    }

    // ── tiny argument helpers ──────────────────────────────────
    static string GetString(string[] args, string key, string def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return def;
    }

    static int GetInt(string[] args, string key, int def) =>
        int.TryParse(GetString(args, key, def.ToString()), out int v) ? v : def;

    static long GetLong(string[] args, string key, long def) =>
        long.TryParse(GetString(args, key, def.ToString()), out long v) ? v : def;
}
