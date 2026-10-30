namespace Common;

using System;
using System.IO;

public static class DataGenerator
{
    /// <summary>
    /// Writes <paramref name="count"/> random integers in [-1 000 000, 1 000 000]
    /// to <paramref name="path"/>, one per line.
    /// </summary>
    public static void Generate(string path, long count, int seed = 0)
    {
        var rng = seed == 0 ? new Random() : new Random(seed);
        using var writer = new StreamWriter(path);
        Span<char> buf = stackalloc char[16];
        for (long i = 0; i < count; i++)
        {
            int v = rng.Next(-1_000_000, 1_000_001);
            writer.WriteLine(v.ToString());
        }
        Console.WriteLine($"Generated {count:N0} integers → {path}");
    }

    /// <summary>
    /// Splits <paramref name="source"/> into <paramref name="parts"/> files
    /// inside <paramref name="outDir"/>, each with an equal number of lines.
    /// </summary>
    public static void Split(string source, string outDir, int parts = 8)
    {
        Directory.CreateDirectory(outDir);
        long total = 0;
        using (var sr = new StreamReader(source))
            while (sr.ReadLine() != null) total++;

        long per = total / parts;
        long done = 0;
        using var reader = new StreamReader(source);
        for (int p = 0; p < parts; p++)
        {
            long take = p == parts - 1 ? total - done : per;
            string dest = Path.Combine(outDir, $"numbers-{p + 1:00}.txt");
            using var w = new StreamWriter(dest);
            for (long i = 0; i < take; i++)
            {
                string? line = reader.ReadLine();
                if (line == null) break;
                w.WriteLine(line);
            }
            done += take;
            Console.WriteLine($"  {dest}  ({take:N0} lines)");
        }
        Console.WriteLine($"Split {total:N0} lines into {parts} files in {outDir}");
    }
}
