namespace Tasks;

using System;

public class Task1B
{
    public static void Run(string[] args)
    {
        Console.WriteLine("Task 1b: three ways to count (shared / lock / thread-local)");
        Console.WriteLine("Usage: 1b [--threads N] [--input numbers.txt] [--csv results.csv]");
        // TODO: load array outside timer, run unsync × 3, lock × 1, thread-local × 1
        Console.WriteLine("Not yet implemented.");
    }
}
