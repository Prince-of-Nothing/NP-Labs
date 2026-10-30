namespace Tasks;

using System;
using System.Collections.Concurrent;

public class Task1C
{
    public static void Run(string[] args)
    {
        Console.WriteLine("Task 1c: producer–consumer pipeline (bounded queue)");
        Console.WriteLine("Usage: 1c [--workers N] [--input numbers.txt] [--csv results.csv]");
        // TODO: reader → BlockingCollection<string[]> (cap 100, batch 10 000) → workers
        Console.WriteLine("Not yet implemented.");
    }
}
