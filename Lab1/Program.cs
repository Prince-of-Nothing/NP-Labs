using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class Program
{
    private const long DefaultNumberCount = 50_000_000;
    private const int DefaultBatchSize = 10_000;
    private const int DefaultQueueCapacity = 100;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            var options = ParseOptions(args, 1);
            switch (args[0])
            {
                case "generate":
                    Generate(options);
                    break;
                case "split":
                    Split(options);
                    break;
                case "task1a":
                    RunTask1A(options);
                    break;
                case "task1b":
                    RunTask1B(options);
                    break;
                case "task1c":
                    RunTask1C(options);
                    break;
                case "task2":
                    RunTask2(options);
                    break;
                case "hardware":
                    ValidateOptions(options);
                    PrintHardware();
                    break;
                default:
                    throw new ArgumentException($"Unknown command: {args[0]}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static void Generate(Dictionary<string, string?> options)
    {
        ValidateOptions(options, "output", "count", "seed", "force");
        var path = GetOption(options, "output", "numbers.txt");
        var count = GetLongOption(options, "count", DefaultNumberCount);
        var seed = GetIntOption(options, "seed", Environment.TickCount);
        var force = options.ContainsKey("force");
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException("count", "Count must be positive.");
        }

        EnsureCanCreate(path, force);
        var random = new Random(seed);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 20);
        for (long index = 0; index < count; index++)
        {
            writer.WriteLine(random.Next(-1_000_000, 1_000_001).ToString(CultureInfo.InvariantCulture));
        }

        Console.WriteLine($"Generated {count.ToString(CultureInfo.InvariantCulture)} integers in {Path.GetFullPath(path)} (seed {seed}).");
    }

    private static void Split(Dictionary<string, string?> options)
    {
        ValidateOptions(options, "input", "output-dir", "files", "force");
        var input = GetOption(options, "input", "numbers.txt");
        var outputDirectory = GetOption(options, "output-dir", "usb");
        var fileCount = GetIntOption(options, "files", 8);
        var force = options.ContainsKey("force");
        if (fileCount < 1)
        {
            throw new ArgumentOutOfRangeException("files", "File count must be positive.");
        }
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Input file was not found.", input);
        }

        Directory.CreateDirectory(outputDirectory);
        var destinations = Enumerable.Range(0, fileCount)
            .Select(index => Path.Combine(outputDirectory, $"numbers-{index + 1:00}.txt"))
            .ToArray();
        foreach (var destination in destinations)
        {
            EnsureCanCreate(destination, force);
        }

        var totalLines = CountLines(input);
        var total = 0L;
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
        var writers = destinations
            .Select(path => new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20), new UTF8Encoding(false), 1 << 20))
            .ToArray();
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var fileIndex = (int)Math.Min((total * fileCount) / Math.Max(1, totalLines), fileCount - 1);
                writers[fileIndex].WriteLine(line);
                total++;
            }
        }
        finally
        {
            foreach (var writer in writers)
            {
                writer.Dispose();
            }
        }

        Console.WriteLine($"Split {total.ToString(CultureInfo.InvariantCulture)} lines into {fileCount} files in {Path.GetFullPath(outputDirectory)}.");
    }

    private static void RunTask1A(Dictionary<string, string?> options)
    {
        ValidateOptions(options, "input", "threads", "expected-count", "csv");
        var input = GetOption(options, "input", "numbers.txt");
        EnsureInputExists(input);
        var fileLength = new FileInfo(input).Length;
        var expected = GetLongOption(options, "expected-count", DefaultNumberCount);
        var results = new List<(int Workers, double ElapsedMilliseconds, long PeakBytes, Counts Counts)>();
        foreach (var threadCount in ParseThreadCounts(options, [1, 2, 4, 8, 16, 32, 64]))
        {
            var ranges = Enumerable.Range(0, threadCount)
                .Select(index => (Start: fileLength * index / threadCount, End: fileLength * (index + 1) / threadCount))
                .ToArray();
            var partial = new Counts[threadCount];
            var errors = new ConcurrentQueue<Exception>();
            var threads = new Thread[threadCount];

            using var measurement = new Measurement();
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < threadCount; index++)
            {
                var workerIndex = index;
                threads[index] = new Thread(() =>
                {
                    try
                    {
                        partial[workerIndex] = CountFileRange(input, ranges[workerIndex].Start, ranges[workerIndex].End, fileLength);
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                    }
                }) { IsBackground = true };
                threads[index].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }
            ThrowWorkerErrors(errors);
            var counts = partial.Aggregate(Counts.Empty, static (sum, item) => sum + item);
            stopwatch.Stop();
            results.Add((threadCount, stopwatch.Elapsed.TotalMilliseconds, measurement.StopAndGetPeakBytes(), counts));
        }
        var oneWorkerTime = results.FirstOrDefault(result => result.Workers == 1).ElapsedMilliseconds;
        foreach (var result in results)
        {
            Report("task1a", "predivided", result.Workers, result.ElapsedMilliseconds, result.PeakBytes,
                result.Counts, expected, options, oneWorkerTime > 0 ? oneWorkerTime / result.ElapsedMilliseconds : null);
        }
    }

    private static void RunTask1B(Dictionary<string, string?> options)
    {
        ValidateOptions(options, "input", "threads", "threads-file", "expected-count", "csv");
        var input = GetOption(options, "input", "numbers.txt");
        EnsureInputExists(input);
        var expected = GetLongOption(options, "expected-count", DefaultNumberCount);
        var threadCount = options.ContainsKey("threads")
            ? GetIntOption(options, "threads", Environment.ProcessorCount)
            : options.TryGetValue("threads-file", out var threadsFile)
                ? ReadThreadCount(threadsFile!)
                : GetLogicalProcessorCount();
        if (threadCount < 1)
        {
            throw new ArgumentOutOfRangeException("threads", "Thread count must be positive.");
        }

        var numberCount = CountLines(input);
        if (numberCount > Array.MaxLength)
        {
            throw new InvalidDataException($"The input has {numberCount} lines, which is too many for a single array.");
        }

        var numbers = new int[(int)numberCount];
        using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20))
        {
            for (var index = 0; index < numbers.Length; index++)
            {
                var line = reader.ReadLine();
                if (line is null || !int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[index]))
                {
                    throw new InvalidDataException($"Invalid or missing integer at line {index + 1}.");
                }
            }
            if (reader.ReadLine() is not null)
            {
                throw new InvalidDataException("The file changed while it was being loaded.");
            }
        }

        for (var run = 1; run <= 3; run++)
        {
            var shared = new long[3];
            var stopwatch = Stopwatch.StartNew();
            using var measurement = new Measurement();
            RunArrayWorkers(numbers, threadCount, (index, value) =>
            {
                if (value < 0)
                {
                    shared[0]++;
                }
                else if (value > 0)
                {
                    shared[1]++;
                }
                else
                {
                    shared[2]++;
                }
            });
            stopwatch.Stop();
            var counts = new Counts(shared[0], shared[1], shared[2], numbers.LongLength);
            Report("task1b", $"unsynchronized-{run}", threadCount, stopwatch.Elapsed.TotalMilliseconds, measurement.StopAndGetPeakBytes(), counts, expected, options);
        }

        var locked = new long[3];
        var gate = new object();
        var lockWatch = Stopwatch.StartNew();
        long lockPeak;
        using (var measurement = new Measurement())
        {
            RunArrayWorkers(numbers, threadCount, (index, value) =>
            {
                lock (gate)
                {
                    if (value < 0)
                    {
                        locked[0]++;
                    }
                    else if (value > 0)
                    {
                        locked[1]++;
                    }
                    else
                    {
                        locked[2]++;
                    }
                }
            });
            lockWatch.Stop();
            lockPeak = measurement.StopAndGetPeakBytes();
        }
        Report("task1b", "locked", threadCount, lockWatch.Elapsed.TotalMilliseconds, lockPeak,
            new Counts(locked[0], locked[1], locked[2], numbers.LongLength), expected, options);

        var localCounts = new Counts[threadCount];
        var localWatch = Stopwatch.StartNew();
        long localPeak;
        Counts total;
        using (var measurement = new Measurement())
        {
            RunArrayWorkersWithLocalCounts(numbers, threadCount, localCounts);
            total = localCounts.Aggregate(Counts.Empty, static (sum, item) => sum + item);
            localWatch.Stop();
            localPeak = measurement.StopAndGetPeakBytes();
        }
        Report("task1b", "thread-local", threadCount, localWatch.Elapsed.TotalMilliseconds, localPeak, total, expected, options);
    }

    private static void RunTask1C(Dictionary<string, string?> options)
    {
        ValidateOptions(options, "input", "threads", "expected-count", "csv");
        var input = GetOption(options, "input", "numbers.txt");
        EnsureInputExists(input);
        var expected = GetLongOption(options, "expected-count", DefaultNumberCount);
        var results = new List<(int Workers, double ElapsedMilliseconds, long PeakBytes, Counts Counts)>();
        foreach (var workerCount in ParseThreadCounts(options, [1, 2, 4, 8, 16, 32, 64]))
        {
            using var batches = new BlockingCollection<string[]>(DefaultQueueCapacity);
            var workerCounts = new Counts[workerCount];
            var workerTasks = new Task[workerCount];
            using var measurement = new Measurement();
            var stopwatch = Stopwatch.StartNew();

            for (var worker = 0; worker < workerCount; worker++)
            {
                var workerIndex = worker;
                workerTasks[worker] = Task.Factory.StartNew(() =>
                {
                    var local = Counts.Empty;
                    foreach (var batch in batches.GetConsumingEnumerable())
                    {
                        foreach (var line in batch)
                        {
                            if (!int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                            {
                                throw new InvalidDataException($"Invalid integer in input: '{line}'.");
                            }
                            local = local.Add(value);
                        }
                    }
                    workerCounts[workerIndex] = local;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            var producer = Task.Factory.StartNew(() =>
            {
                try
                {
                    using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
                    var batch = new List<string>(DefaultBatchSize);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        batch.Add(line);
                        if (batch.Count == DefaultBatchSize)
                        {
                            batches.Add(batch.ToArray());
                            batch.Clear();
                        }
                    }
                    if (batch.Count > 0)
                    {
                        batches.Add(batch.ToArray());
                    }
                }
                finally
                {
                    batches.CompleteAdding();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Task.WaitAll(workerTasks.Append(producer).ToArray());
            var counts = workerCounts.Aggregate(Counts.Empty, static (sum, item) => sum + item);
            stopwatch.Stop();
            results.Add((workerCount, stopwatch.Elapsed.TotalMilliseconds, measurement.StopAndGetPeakBytes(), counts));
        }
        var oneWorkerTime = results.FirstOrDefault(result => result.Workers == 1).ElapsedMilliseconds;
        foreach (var result in results)
        {
            Report("task1c", "bounded-batches-10000", result.Workers, result.ElapsedMilliseconds,
                result.PeakBytes, result.Counts, expected, options,
                oneWorkerTime > 0 ? oneWorkerTime / result.ElapsedMilliseconds : null);
        }
    }

    private static void RunTask2(Dictionary<string, string?> options)
    {
        ValidateOptions(options, "input-dir", "files", "threads", "expected-count", "csv");
        var inputDirectory = GetOption(options, "input-dir", "usb");
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException($"Input directory was not found: {inputDirectory}");
        }

        var files = Directory.GetFiles(inputDirectory, "numbers-*.txt")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var expectedFiles = GetIntOption(options, "files", 8);
        if (files.Length != expectedFiles)
        {
            throw new InvalidDataException($"Expected {expectedFiles} files named numbers-*.txt in '{inputDirectory}', found {files.Length}.");
        }

        var expected = GetLongOption(options, "expected-count", DefaultNumberCount);
        var results = new List<(int Workers, double ElapsedMilliseconds, long PeakBytes, Counts Counts)>();
        foreach (var workerCount in ParseThreadCounts(options, [1, 2, 4]))
        {
            var assignments = DistributeFilesBySize(files, workerCount);
            var workerCounts = new Counts[workerCount];
            using var measurement = new Measurement();
            var stopwatch = Stopwatch.StartNew();
            Parallel.For(0, workerCount, worker =>
            {
                var local = Counts.Empty;
                foreach (var file in assignments[worker])
                {
                    local += CountBuffer(File.ReadAllBytes(file));
                }
                workerCounts[worker] = local;
            });
            var counts = workerCounts.Aggregate(Counts.Empty, static (sum, item) => sum + item);
            stopwatch.Stop();
            results.Add((workerCount, stopwatch.Elapsed.TotalMilliseconds, measurement.StopAndGetPeakBytes(), counts));
        }
        var oneWorkerTime = results.FirstOrDefault(result => result.Workers == 1).ElapsedMilliseconds;
        foreach (var result in results)
        {
            Report("task2", "whole-file-buffer", result.Workers, result.ElapsedMilliseconds, result.PeakBytes,
                result.Counts, expected, options, oneWorkerTime > 0 ? oneWorkerTime / result.ElapsedMilliseconds : null);
        }
    }

    private static Counts CountFileRange(string path, long start, long end, long fileLength)
    {
        if (start >= end || start >= fileLength)
        {
            return Counts.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (start > 0)
        {
            stream.Position = start - 1;
            if (stream.ReadByte() != '\n')
            {
                stream.Position = start;
                while (stream.ReadByte() is var value && value != -1 && value != '\n')
                {
                }
            }
            else
            {
                stream.Position = start;
            }
        }
        else
        {
            stream.Position = 0;
        }

        var absolutePosition = stream.Position;
        var lineStart = absolutePosition;
        var buffer = new byte[1 << 20];
        var counts = Counts.Empty;
        var valueAccumulator = 0;
        var negative = false;
        var hasDigit = false;
        int bytesRead;
        while (lineStart < end && (bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < bytesRead && lineStart < end; index++)
            {
                var current = buffer[index];
                absolutePosition++;
                if (current == (byte)'\n')
                {
                    counts = counts.AddParsed(valueAccumulator, negative, hasDigit);
                    valueAccumulator = 0;
                    negative = false;
                    hasDigit = false;
                    lineStart = absolutePosition;
                }
                else if (current == (byte)'-' && !hasDigit && valueAccumulator == 0)
                {
                    negative = true;
                }
                else if (current is >= (byte)'0' and <= (byte)'9')
                {
                    hasDigit = true;
                    valueAccumulator = checked(valueAccumulator * 10 + current - (byte)'0');
                }
                else if (current != (byte)'\r')
                {
                    throw new InvalidDataException($"Unexpected byte 0x{current:X2} in input file.");
                }
            }
        }

        if (lineStart < end && lineStart < fileLength)
        {
            counts = counts.AddParsed(valueAccumulator, negative, hasDigit);
        }
        return counts;
    }

    private static Counts CountBuffer(byte[] bytes)
    {
        var counts = Counts.Empty;
        var valueAccumulator = 0;
        var negative = false;
        var hasDigit = false;
        foreach (var current in bytes)
        {
            if (current == (byte)'\n')
            {
                counts = counts.AddParsed(valueAccumulator, negative, hasDigit);
                valueAccumulator = 0;
                negative = false;
                hasDigit = false;
            }
            else if (current == (byte)'-' && !hasDigit && valueAccumulator == 0)
            {
                negative = true;
            }
            else if (current is >= (byte)'0' and <= (byte)'9')
            {
                hasDigit = true;
                valueAccumulator = checked(valueAccumulator * 10 + current - (byte)'0');
            }
            else if (current != (byte)'\r')
            {
                throw new InvalidDataException($"Unexpected byte 0x{current:X2} in input file.");
            }
        }
        if (hasDigit)
        {
            counts = counts.AddParsed(valueAccumulator, negative, hasDigit);
        }
        return counts;
    }

    private static void RunArrayWorkers(int[] numbers, int threadCount, Action<int, int> action)
    {
        var errors = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var worker = index;
            var start = numbers.Length * worker / threadCount;
            var end = numbers.Length * (worker + 1) / threadCount;
            threads[worker] = new Thread(() =>
            {
                try
                {
                    for (var item = start; item < end; item++)
                    {
                        action(worker, numbers[item]);
                    }
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }) { IsBackground = true };
            threads[worker].Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }
        ThrowWorkerErrors(errors);
    }

    private static void RunArrayWorkersWithLocalCounts(int[] numbers, int threadCount, Counts[] results)
    {
        var errors = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];
        for (var index = 0; index < threadCount; index++)
        {
            var worker = index;
            var start = numbers.Length * worker / threadCount;
            var end = numbers.Length * (worker + 1) / threadCount;
            threads[worker] = new Thread(() =>
            {
                try
                {
                    var local = Counts.Empty;
                    for (var item = start; item < end; item++)
                    {
                        local = local.Add(numbers[item]);
                    }
                    results[worker] = local;
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }) { IsBackground = true };
            threads[worker].Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }
        ThrowWorkerErrors(errors);
    }

    private static List<string>[] DistributeFilesBySize(string[] files, int workerCount)
    {
        var assignments = Enumerable.Range(0, workerCount).Select(_ => new List<string>()).ToArray();
        var loads = new long[workerCount];
        foreach (var file in files.OrderByDescending(path => new FileInfo(path).Length))
        {
            var worker = Array.IndexOf(loads, loads.Min());
            assignments[worker].Add(file);
            loads[worker] += new FileInfo(file).Length;
        }
        return assignments;
    }

    private static long CountLines(string path)
    {
        long lines = 0;
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
        while (reader.ReadLine() is not null)
        {
            lines++;
        }
        return lines;
    }

    private static void Report(
        string task,
        string configuration,
        int workers,
        double elapsedMilliseconds,
        long peakBytes,
        Counts counts,
        long expected,
        Dictionary<string, string?> options,
        double? speedup = null)
    {
        var correct = counts.Negative + counts.Positive + counts.Zero == counts.Lines && counts.Lines == expected;
        var row = new ResultRow(task, configuration, workers, elapsedMilliseconds,
            peakBytes / (1024d * 1024d), counts.Negative, counts.Positive, counts.Zero, counts.Lines, expected, correct, speedup);
        Console.WriteLine($"{row.Task},{row.Configuration},{row.Workers},{row.ElapsedMilliseconds:F3},{row.PeakMemoryMiB:F2},{row.Negative},{row.Positive},{row.Zero},{row.Total},{row.Expected},{row.Correct},{row.Speedup?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty}");
        if (options.TryGetValue("csv", out var csvPath) && !string.IsNullOrWhiteSpace(csvPath))
        {
            var fullPath = Path.GetFullPath(csvPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var writeHeader = !File.Exists(fullPath) || new FileInfo(fullPath).Length == 0;
            using var writer = new StreamWriter(fullPath, append: true, new UTF8Encoding(false));
            if (writeHeader)
            {
                writer.WriteLine("task,configuration,workers,elapsed_ms,peak_memory_mib,negative,positive,zero,total,expected,correct,speedup");
            }
            writer.WriteLine($"{row.Task},{row.Configuration},{row.Workers},{row.ElapsedMilliseconds.ToString("F3", CultureInfo.InvariantCulture)},{row.PeakMemoryMiB.ToString("F2", CultureInfo.InvariantCulture)},{row.Negative},{row.Positive},{row.Zero},{row.Total},{row.Expected},{row.Correct.ToString().ToLowerInvariant()},{row.Speedup?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty}");
        }
    }

    private static void PrintHardware()
    {
        Console.WriteLine($"Operating system: {Environment.OSVersion}");
        Console.WriteLine($"Runtime: {Environment.Version}");
        Console.WriteLine($"Logical processors available to .NET: {Environment.ProcessorCount}");
        Console.WriteLine($"Processor identifier: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "not available"}");
        Console.WriteLine($"Memory available to process: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024d * 1024d):F2} GiB");
        Console.WriteLine("Use the README PowerShell commands to record CPU model/core count, installed RAM, and disk/USB details.");
    }

    private static int[] ParseThreadCounts(Dictionary<string, string?> options, int[] defaults)
    {
        if (!options.TryGetValue("threads", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaults;
        }

        var counts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"Invalid thread count: '{item}'."))
            .Distinct()
            .ToArray();
        if (counts.Length == 0)
        {
            throw new ArgumentException("At least one thread count is required.");
        }
        return counts;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args, int startIndex)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = startIndex; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {token}");
            }
            var name = token[2..];
            if (name == "force")
            {
                options[name] = null;
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[name] = args[++index];
            }
            else
            {
                throw new ArgumentException($"Option --{name} requires a value.");
            }
        }
        return options;
    }

    private static void ValidateOptions(Dictionary<string, string?> options, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var unknown = options.Keys.Where(option => !allowedSet.Contains(option)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown.Select(option => $"--{option}"))}.");
        }
    }

    private static string GetOption(Dictionary<string, string?> options, string name, string defaultValue) =>
        options.TryGetValue(name, out var value) && value is not null ? value : defaultValue;

    private static int GetIntOption(Dictionary<string, string?> options, string name, int defaultValue) =>
        options.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : options.ContainsKey(name) ? throw new ArgumentException($"Invalid integer for --{name}.") : defaultValue;

    private static long GetLongOption(Dictionary<string, string?> options, string name, long defaultValue) =>
        options.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : options.ContainsKey(name) ? throw new ArgumentException($"Invalid integer for --{name}.") : defaultValue;

    private static int GetLogicalProcessorCount()
    {
        var count = Environment.ProcessorCount;
        if (count > 0)
        {
            return count;
        }

        const string fallbackPath = "threads.txt";
        if (!File.Exists(fallbackPath))
        {
            throw new InvalidOperationException(
                $"The runtime did not report a valid logical processor count. Create {fallbackPath} with one positive integer.");
        }
        return ReadThreadCount(fallbackPath);
    }

    private static int ReadThreadCount(string path)
    {
        var contents = File.ReadAllText(path).Trim();
        if (!int.TryParse(contents, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            throw new InvalidDataException($"'{path}' must contain one positive integer.");
        }
        return count;
    }

    private static void EnsureInputExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Input file was not found.", path);
        }
    }

    private static void EnsureCanCreate(string path, bool force)
    {
        if (File.Exists(path) && !force)
        {
            throw new IOException($"'{path}' already exists. Pass --force to overwrite it.");
        }
    }

    private static void ThrowWorkerErrors(ConcurrentQueue<Exception> errors)
    {
        if (!errors.IsEmpty)
        {
            throw new AggregateException("One or more workers failed.", errors);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Laboratory Work 1 - Concurrency in Practice

            Commands:
              generate [--output numbers.txt] [--count 50000000] [--seed N] [--force]
              split [--input numbers.txt] [--output-dir usb] [--files 8] [--force]
              task1a [--input numbers.txt] [--threads 1,2,4,8,16,32,64] [--expected-count 50000000] [--csv results.csv]
              task1b [--input numbers.txt] [--threads LOGICAL_PROCESSOR_COUNT | --threads-file threads.txt] [--expected-count 50000000] [--csv results.csv]
              task1c [--input numbers.txt] [--threads 1,2,4,8,16,32,64] [--expected-count 50000000] [--csv results.csv]
              task2 [--input-dir usb] [--files 8] [--threads 1,2,4] [--expected-count 50000000] [--csv results.csv]
              hardware

            Build and run with:
              dotnet build -c Release
              dotnet run -c Release -- generate
            """);
    }

    private readonly record struct Counts(long Negative, long Positive, long Zero, long Lines)
    {
        public static Counts Empty => new(0, 0, 0, 0);

        public Counts Add(int value) => value < 0
            ? this with { Negative = Negative + 1, Lines = Lines + 1 }
            : value > 0
                ? this with { Positive = Positive + 1, Lines = Lines + 1 }
                : this with { Zero = Zero + 1, Lines = Lines + 1 };

        public Counts AddParsed(int magnitude, bool negative, bool hasDigit)
        {
            if (!hasDigit)
            {
                throw new InvalidDataException("An input line does not contain an integer.");
            }
            return Add(negative ? -magnitude : magnitude);
        }

        public static Counts operator +(Counts left, Counts right) =>
            new(left.Negative + right.Negative, left.Positive + right.Positive, left.Zero + right.Zero, left.Lines + right.Lines);
    }

    private sealed record ResultRow(
        string Task,
        string Configuration,
        int Workers,
        double ElapsedMilliseconds,
        double PeakMemoryMiB,
        long Negative,
        long Positive,
        long Zero,
        long Total,
        long Expected,
        bool Correct,
        double? Speedup);

    private sealed class Measurement : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Thread _sampler;
        private volatile bool _running = true;
        private long _peakBytes;

        public Measurement()
        {
            Sample();
            _sampler = new Thread(() =>
            {
                while (_running)
                {
                    Sample();
                    Thread.Sleep(5);
                }
                Sample();
            }) { IsBackground = true };
            _sampler.Start();
        }

        public long PeakBytes => Interlocked.Read(ref _peakBytes);

        public long StopAndGetPeakBytes()
        {
            if (_running)
            {
                _running = false;
                _sampler.Join();
                Sample();
            }
            return PeakBytes;
        }

        private void Sample()
        {
            var workingSet = _process.WorkingSet64;
            long previous;
            do
            {
                previous = Interlocked.Read(ref _peakBytes);
                if (workingSet <= previous)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref _peakBytes, workingSet, previous) != previous);
        }

        public void Dispose()
        {
            StopAndGetPeakBytes();
            _process.Dispose();
        }
    }
}
