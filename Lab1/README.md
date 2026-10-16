# NP-Labs — Laboratory Work 1

This repository contains one .NET 8 command-line program for the concurrency
experiments. It generates the input, prepares the eight Task 2 files, runs Tasks
1a/1b/1c and 2, and emits counts, elapsed time, and sampled peak working-set
memory. `./usb/` is intentionally ignored by Git; it is a staging folder only.

## Build

Install the .NET 8 SDK, then run these commands from the `Lab1` directory:

```powershell
dotnet build -c Release
dotnet run -c Release -- help
```

## Generate the input and prepare USB files

The defaults create exactly 50,000,000 random integers in `numbers.txt`,
uniformly selected from -1,000,000 through 1,000,000. The file is not checked
into Git. Use `--seed` when a repeatable dataset is useful; omit it for a
different dataset each time.

```powershell
dotnet run -c Release --no-build -- generate
dotnet run -c Release --no-build -- split
```

`split` writes `usb/numbers-01.txt` through `usb/numbers-08.txt`, in input
order, with 6,250,000 lines per file. Copy those eight files to the USB flash
drive. For repeat runs, `--force` explicitly permits replacing generated
outputs. To test with a smaller input, supply `--count` when generating and
pass the same `--expected-count` to each task.

## Run and record experiments

All task commands accept `--csv results.csv`. The program appends results to a
single CSV with a header on its first write. Run Task 1a and 1c with the full
thread-count series, then Task 1b once at the logical-processor count:

```powershell
dotnet run -c Release --no-build -- task1a --csv results.csv
dotnet run -c Release --no-build -- task1b --csv results.csv
dotnet run -c Release --no-build -- task1c --csv results.csv
```

Task 1b automatically gets the logical processor count from
`Environment.ProcessorCount` when `--threads` is omitted. This is supported on
Windows and Linux. If you want to set the count manually from a text file, put
one positive integer (for example, `16`) in `threads.txt` and run:

```powershell
dotnet run -c Release --no-build -- task1b --threads-file threads.txt --csv results.csv
```

The file is only needed when explicitly using `--threads-file`, or as a
fallback if the runtime ever reports an invalid processor count. An explicit
`--threads` value takes precedence over the file.

For Task 2, eject and unplug the drive, plug it back in before each
configuration, and pass the directory containing the eight files. Run one
configuration per replug, as required by the lab:

```powershell
dotnet run -c Release --no-build -- task2 --input-dir E:\ --threads 1 --csv results.csv
```

Repeat with `--threads 2` and `--threads 4`. Replace `E:\` with the actual
drive letter. The program assigns whole files to workers, balancing by file
size, reads each file into one byte buffer, and parses that buffer.

Task 2 also writes `task2-baseline-1thread.txt` next to the CSV with the one-thread
elapsed time, so a later single-configuration run (after a replug) can still report
a speed-up. Delete that file whenever you change the data set or the drive; it is
only valid for the measurements taken on the drive and data set that produced it.

The CSV's `correct` field is true only when the three sign counts sum to the
number of lines processed and the processed-line count equals
`--expected-count` (50,000,000 by default). Unsynchronized Task 1b results can
show lost updates; record the actual totals because the race is nondeterministic.
The `speedup` field is relative to the one-worker run in the same invocation
for Tasks 1a, 1c, and 2. Task 1b has one configured worker count and leaves
speed-up blank; compare its three timing rows directly.

Peak memory is the maximum process working set sampled every 5 ms during the
measured operation. Task 1b's array is loaded before timing but remains in
memory during the measurement, as required. The operating system's file cache
is outside the process working set and is not included.

## Record hardware

Run `dotnet run -c Release --no-build -- hardware` for the OS, runtime, and
logical processor count visible to .NET. On Windows, record the remaining
hardware details with:

```powershell
Get-CimInstance Win32_Processor |
  Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
Get-CimInstance Win32_ComputerSystem |
  Select-Object @{Name='RAM_GiB';Expression={[math]::Round($_.TotalPhysicalMemory / 1GB, 2)}}
Get-PhysicalDisk | Select-Object FriendlyName, MediaType, BusType, Size
Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber
```

On Linux, build and run the same .NET 8 project with `dotnet build -c Release`
and `dotnet run -c Release -- task1a --csv results.csv`. `Environment.ProcessorCount`,
file I/O, threads, and the memory sampler are cross-platform. Use Linux tools
such as `lscpu`, `free -h`, `lsblk -o NAME,MODEL,ROTA,TRAN,SIZE`, and
`uname -a` to record hardware and OS details. Use the mounted USB path (for
example, `/media/$USER/LABEL`) for Task 2; unplug/replug the drive between
configurations as required.

Also record the USB flash-drive model and USB version (for example, from the
drive manufacturer's specification or USB Device Tree Viewer), the drive's
capacity, and which physical disk was used for `numbers.txt`. Include the
language/runtime (.NET 8) and the output of the `hardware` command in the
report.

## Implementation notes

- Task 1a partitions the input by byte ranges. Each worker seeks to its own
  range, aligns to a line boundary, and counts each line whose starting byte
  belongs to that range; no single worker scans the whole input.
- Task 1b loads the array outside the timer and runs the intentionally
  unsynchronized shared counters, locked shared counters, and thread-local
  counters. The unsynchronized run is repeated three times.
- Task 1c uses one line-reading producer and a bounded, thread-safe queue of
  at most 100 batches, each containing up to 10,000 lines.
- Task 2 uses each file as the work item and uses whole-file byte buffers.
