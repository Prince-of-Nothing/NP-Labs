namespace Tasks;

using System;

public class Task2
{
    public static void Run(string[] args)
    {
        Console.WriteLine("Task 2: multi-file processing on slow storage (USB)");
        Console.WriteLine("Usage: 2 [--dir <usb path>] [--threads N] [--csv results.csv]");
        // TODO: whole-file ReadAllBytes, size-balanced assignment across N threads
        Console.WriteLine("Not yet implemented.");
    }
}
