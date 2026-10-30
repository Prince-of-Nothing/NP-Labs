#Requires -Version 5.1
<#
.SYNOPSIS
  Lab 1 Swiss-army-knife script for Windows. lab.sh provides the same verbs for Linux/CachyOS.

  Usage:
    powershell -ExecutionPolicy Bypass -File scripts\lab.ps1 <command> [options]

  Commands:
    check                  Verify prerequisites (.NET 8 SDK, git, python + matplotlib, free disk space)
    status                 Show which generated files exist and what is in results.csv
    build                  Build the project (Release)
    generate [--count N] [--seed N] [--force]
                           Write Lab1\numbers.txt (50,000,000 integers by default)
    split [--files N] [--force]
                           Split numbers.txt into Lab1\usb\numbers-01..08.txt
    task1a [args...]       Task 1a: counting with predivided file (1,2,4,8,16,32,64 threads)
    task1b [args...]       Task 1b: three ways to count (logical-processor count)
    task1c [args...]       Task 1c: producer and consumers (1..64 workers)
    task1 [args...]        Run task1a + task1b + task1c in order
                           (task1b always uses the logical-processor count)
    task2 --dir <path> [--threads 1,2,4] [--no-pause] [args...]
                           Task 2 from the USB drive; pauses between configurations
                           so you can eject/unplug/replug as the lab requires
    hardware               Write hardware.txt (dotnet hardware + CPU/RAM/disk/OS details)
    verify [--csv path]    Cross-check totals of every correct row in the CSV
    charts [--csv path] [--out dir]
                           Generate report charts (task1a/b/c/2 .png) with matplotlib
    all [--force]          check + build + generate + split + task1 + hardware + verify + charts
                           (everything that does not need the manual USB replug)
    clean [--all]          Remove bin/ and obj/; --all also removes generated data
                           (numbers.txt, usb/, charts/, hardware.txt, results.csv)

  Any extra arguments are forwarded verbatim to the Lab1 program, e.g.
    scripts\lab.ps1 task1a --threads 1,2,4 --csv D:\runs\results.csv --expected-count 200000
#>
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$LabDir = Join-Path $RootDir 'Lab1'
$NumbersFile = Join-Path $LabDir 'numbers.txt'
$UsbDir = Join-Path $LabDir 'usb'
$DefaultCsv = Join-Path $LabDir 'results.csv'
$HardwareFile = Join-Path $RootDir 'hardware.txt'
$ChartsScript = Join-Path $ScriptDir 'charts.py'

$Command = if ($args.Count -gt 0) { $args[0] } else { 'help' }
$Rest = [System.Collections.Generic.List[string]]::new()
for ($index = 1; $index -lt $args.Count; $index++) { $Rest.Add($args[$index]) }

function Take-Option {
    param([System.Collections.Generic.List[string]]$Bag, [string]$Name)
    $index = $Bag.IndexOf("--$Name")
    if ($index -lt 0) { return $null }
    if ($index + 1 -ge $Bag.Count) { throw "Option --$Name requires a value." }
    $value = $Bag[$index + 1]
    $Bag.RemoveRange($index, 2)
    return $value
}

function Take-Flag {
    param([System.Collections.Generic.List[string]]$Bag, [string]$Name)
    $index = $Bag.IndexOf("--$Name")
    if ($index -lt 0) { return $false }
    $Bag.RemoveAt($index)
    return $true
}

function Get-Leftover {
    param([System.Collections.Generic.List[string]]$Bag)
    return ,($Bag.ToArray())
}

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Dotnet {
    param([string[]]$DotnetArgs, [switch]$Raw)
    Push-Location $LabDir
    try {
        if ($Raw) { & dotnet @DotnetArgs } else { & dotnet run -c Release --no-build -- @DotnetArgs }
        if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Lab([string[]]$LabArgs) {
    Invoke-Dotnet -DotnetArgs $LabArgs
}

function Build {
    Write-Step 'Building Lab1 (Release)'
    Invoke-Dotnet -Raw -DotnetArgs @('build', '-c', 'Release', '--nologo')
}

function Ensure-Build {
    $dll = Join-Path $LabDir 'bin\Release\net8.0\Lab1.dll'
    $sources = @(Get-Item (Join-Path $LabDir 'Program.cs'), (Join-Path $LabDir 'Lab1.csproj'))
    $newest = ($sources | Sort-Object LastWriteTime | Select-Object -Last 1).LastWriteTime
    if (-not (Test-Path $dll) -or (Get-Item $dll).LastWriteTime -lt $newest) {
        Build
    }
}

function Test-Dotnet {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        return $false
    }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match '^8\.' })
}

function Invoke-Check {
    Write-Step 'Checking prerequisites'
    $failed = $false

    if (Test-Dotnet) {
        Write-Host ("  [ok] .NET 8 SDK: " + (& dotnet --version))
    }
    else {
        Write-Host '  [!!] .NET 8 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Red
        $failed = $true
    }

    if (Get-Command git -ErrorAction SilentlyContinue) {
        Write-Host ('  [ok] git: ' + (& git --version))
    }
    else {
        Write-Host '  [~~] git not found (needed for the repository link in the report)' -ForegroundColor Yellow
    }

    $python = $null
    foreach ($candidate in @(@('py', '-3'), @('python'))) {
        if (Get-Command $candidate[0] -ErrorAction SilentlyContinue) {
            $python = $candidate
            break
        }
    }
    if ($python) {
        & $python[0] $python[1] -c 'import matplotlib' 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host '  [ok] python + matplotlib (charts)'
        }
        else {
            Write-Host '  [~~] python found but matplotlib missing: py -3 -m pip install matplotlib (needed for charts)' -ForegroundColor Yellow
        }
    }
    else {
        Write-Host '  [~~] python not found (needed for charts): install Python 3 + matplotlib' -ForegroundColor Yellow
    }

    try {
        $freeGiB = [math]::Round((Get-PSDrive -Name $RootDir[0]).Free / 1GB, 1)
        if ($freeGiB -ge 2) {
            Write-Host "  [ok] $freeGiB GiB free on $($RootDir[0]): (numbers.txt + split copies need ~1 GiB)"
        }
        else {
            Write-Host "  [~~] only $freeGiB GiB free on $($RootDir[0]): generating 50M numbers may fail" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host '  [~~] could not check free disk space' -ForegroundColor Yellow
    }

    if ($failed) { exit 1 }
    Write-Host 'Prerequisites OK.'
}

function Invoke-Status {
    Write-Step 'Status'
    if (Test-Path $NumbersFile) {
        $size = [math]::Round((Get-Item $NumbersFile).Length / 1MB, 1)
        Write-Host ("  numbers.txt: {0} MiB" -f $size)
    }
    else {
        Write-Host '  numbers.txt: missing (run: lab.ps1 generate)'
    }

    $files = @(Get-ChildItem (Join-Path $UsbDir 'numbers-*.txt') -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*-summary.txt' })
    if ($files.Count -gt 0) {
        $size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host ("  usb/: {0} files, {1} MiB total" -f $files.Count, $size)
    }
    else {
        Write-Host '  usb/: no split files (run: lab.ps1 split)'
    }

    if (Test-Path $DefaultCsv) {
        $rows = @(Import-Csv $DefaultCsv)
        $tasks = ($rows | Group-Object task | ForEach-Object { "$($_.Name) ($($_.Count))" }) -join ', '
        Write-Host ("  results.csv: {0} rows [{1}]" -f $rows.Count, $tasks)
    }
    else {
        Write-Host '  results.csv: missing (run some tasks)'
    }

    if (Test-Path $HardwareFile) {
        Write-Host "  hardware.txt: present"
    }
    else {
        Write-Host '  hardware.txt: missing (run: lab.ps1 hardware)'
    }
}

function Invoke-Generate {
    $force = Take-Flag $Rest 'force'
    $extra = [System.Collections.Generic.List[string]]::new()
    $count = Take-Option $Rest 'count'
    if ($count) { $extra.Add('--count'); $extra.Add($count) }
    $seed = Take-Option $Rest 'seed'
    if ($seed) { $extra.Add('--seed'); $extra.Add($seed) }
    $output = Take-Option $Rest 'output'
    if ($output) { $extra.Add('--output'); $extra.Add($output) }
    if ($force) { $extra.Add('--force') }
    foreach ($leftover in (Get-Leftover $Rest)) { $extra.Add($leftover) }

    Ensure-Build
    Write-Step 'Generating numbers.txt'
    Invoke-Lab (@('generate') + $extra.ToArray())
}

function Invoke-Split {
    $force = Take-Flag $Rest 'force'
    $extra = [System.Collections.Generic.List[string]]::new()
    $files = Take-Option $Rest 'files'
    if ($files) { $extra.Add('--files'); $extra.Add($files) }
    $input = Take-Option $Rest 'input'
    if ($input) { $extra.Add('--input'); $extra.Add($input) }
    $outputDir = Take-Option $Rest 'output-dir'
    if ($outputDir) { $extra.Add('--output-dir'); $extra.Add($outputDir) }
    if ($force) { $extra.Add('--force') }
    foreach ($leftover in (Get-Leftover $Rest)) { $extra.Add($leftover) }

    Ensure-Build
    Write-Step 'Splitting numbers.txt into usb/'
    Invoke-Lab (@('split') + $extra.ToArray())
}

function Invoke-TaskCommand([string]$Task) {
    $extra = [System.Collections.Generic.List[string]]::new()
    $csv = Take-Option $Rest 'csv'
    if ($csv) { $extra.Add('--csv'); $extra.Add($csv) }
    else { $extra.Add('--csv'); $extra.Add($DefaultCsv) }
    foreach ($leftover in (Get-Leftover $Rest)) { $extra.Add($leftover) }

    Ensure-Build
    Write-Step "Running $Task"
    Invoke-Lab (@($Task) + $extra.ToArray())
}

function Invoke-Task1 {
    foreach ($task in @('task1a', 'task1b', 'task1c')) {
        $Rest = [System.Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $SavedTask1Rest.Count; $index++) { $Rest.Add($SavedTask1Rest[$index]) }
        if ($task -eq 'task1b') {
            # task1b takes a single thread count; the lab wants the logical-processor count.
            $null = Take-Option $Rest 'threads'
        }
        Invoke-TaskCommand $task
    }
}

function Invoke-Task2 {
    $directory = Take-Option $Rest 'dir'
    if (-not $directory) {
        throw "task2 needs --dir <folder with numbers-01..08.txt on the USB drive>, e.g. --dir E:\ (check with Get-Volume)."
    }
    if (-not (Test-Path $directory)) {
        throw "Directory not found: $directory"
    }
    $pause = -not (Take-Flag $Rest 'no-pause')
    $csv = Take-Option $Rest 'csv'
    $threadsOption = Take-Option $Rest 'threads'
    $series = if ($threadsOption) { @($threadsOption) } else { @('1', '2', '4') }
    $extra = Get-Leftover $Rest

    Ensure-Build
    foreach ($threadCount in $series) {
        if ($pause) {
            Write-Host ''
            Write-Host "Task 2 with $threadCount thread(s):" -ForegroundColor Cyan
            Write-Host '  1. Eject the drive (Safely Remove Hardware), unplug it.'
            Write-Host '  2. Plug it back in so no data comes from the file cache.'
            [void](Read-Host '  Press Enter to run this configuration')
        }
        $arguments = [System.Collections.Generic.List[string]]::new()
        $arguments.Add('task2')
        $arguments.Add('--input-dir'); $arguments.Add($directory)
        $arguments.Add('--threads'); $arguments.Add($threadCount)
        if ($csv) { $arguments.Add('--csv'); $arguments.Add($csv) } else { $arguments.Add('--csv'); $arguments.Add($DefaultCsv) }
        foreach ($leftover in $extra) { $arguments.Add($leftover) }
        Write-Step "Running task2 ($threadCount thread(s))"
        Invoke-Lab $arguments.ToArray()
    }
}

function Invoke-Hardware {
    Ensure-Build
    Write-Step 'Collecting hardware details'
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Captured: $(Get-Date -Format o)")
    $lines.Add('')

    Push-Location $LabDir
    try {
        $lines.Add('--- dotnet hardware ---')
        $lines.AddRange([string[]](& dotnet run -c Release --no-build -- hardware))
    }
    finally {
        Pop-Location
    }
    $lines.Add('')
    $lines.Add('--- Windows details ---')
    try {
        $cpu = Get-CimInstance Win32_Processor
        foreach ($item in $cpu) {
            $lines.Add("CPU: $($item.Name)")
            $lines.Add("Physical cores: $($item.NumberOfCores)")
            $lines.Add("Logical processors: $($item.NumberOfLogicalProcessors)")
        }
        $system = Get-CimInstance Win32_ComputerSystem
        $lines.Add("RAM: $([math]::Round($system.TotalPhysicalMemory / 1GB, 2)) GiB")
        foreach ($disk in @(Get-PhysicalDisk)) {
            $lines.Add("Disk: $($disk.FriendlyName) | $($disk.MediaType) | $($disk.BusType) | $([math]::Round($disk.Size / 1GB, 1)) GiB")
        }
        $os = Get-CimInstance Win32_OperatingSystem
        $lines.Add("OS: $($os.Caption) $($os.Version) build $($os.BuildNumber)")
    }
    catch {
        $lines.Add("Detail collection failed: $($_.Exception.Message)")
    }
    $lines.Add('')
    $lines.Add('Record the USB flash-drive model, its USB version and capacity separately (from the drive spec or USB Device Tree Viewer).')

    Set-Content -Path $HardwareFile -Value $lines
    Get-Content $HardwareFile | Write-Host
    Write-Host ''
    Write-Host "Wrote $HardwareFile"
}

function Invoke-Verify {
    $csv = Take-Option $Rest 'csv'
    if (-not $csv) { $csv = $DefaultCsv }
    if (-not (Test-Path $csv)) { throw "CSV not found: $csv" }

    Write-Step "Verifying $csv"
    $rows = @(Import-Csv $csv)
    if ($rows.Count -eq 0) { throw 'The CSV has no data rows.' }

    $correct = @($rows | Where-Object { $_.correct -eq 'true' })
    $incorrect = @($rows | Where-Object { $_.correct -ne 'true' })
    if ($correct.Count -eq 0) { throw 'No correct rows found - check the measurements.' }

    $groups = @($correct | Group-Object negative, positive, zero, total)
    if ($groups.Count -ne 1) {
        Write-Host 'MISMATCH: correct rows disagree on the totals:' -ForegroundColor Red
        foreach ($group in $groups) {
            Write-Host ("  {0} rows: {1}" -f $group.Count, $group.Group[0])
        }
        exit 1
    }

    $canonical = $correct[0]
    Write-Host ("All {0} correct rows agree: negative={1} positive={2} zero={3} total={4} (expected {5})" -f
        $correct.Count, $canonical.negative, $canonical.positive, $canonical.zero, $canonical.total, $canonical.expected)
    if ($canonical.total -ne $canonical.expected) {
        Write-Host "MISMATCH: total $($canonical.total) != expected $($canonical.expected)" -ForegroundColor Red
        exit 1
    }

    foreach ($row in $incorrect) {
        if ($row.configuration -like 'unsynchronized*') {
            Write-Host ("  [info] {0}/{1} workers {2}: incorrect as designed (race condition); record the actual totals." -f
                $row.task, $row.workers, $row.configuration)
        }
        else {
            Write-Host ("  [!!] {0}/{1} workers {2} is incorrect - investigate before the report." -f
                $row.task, $row.workers, $row.configuration) -ForegroundColor Red
            exit 1
        }
    }
    Write-Host 'Verification passed.'
}

function Invoke-Charts {
    $csv = Take-Option $Rest 'csv'
    if (-not $csv) { $csv = $DefaultCsv }
    $out = Take-Option $Rest 'out'
    if (-not $out) { $out = Join-Path $LabDir 'charts' }

    Write-Step "Generating charts from $csv"
    $arguments = @($ChartsScript, '--csv', $csv, '--out', $out)
    if (Get-Command py -ErrorAction SilentlyContinue) {
        & py -3 @arguments
    }
    else {
        & python @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw 'Chart generation failed.' }
}

function Invoke-All {
    $SavedTask1Rest = @()
    $force = Take-Flag $Rest 'force'
    Invoke-Check
    Build

    if ((Test-Path $NumbersFile) -and -not $force) {
        Write-Step 'numbers.txt already exists - skipping generate (use --force to redo)'
    }
    else {
        $Rest = [System.Collections.Generic.List[string]]::new()
        if ($force) { $Rest.Add('--force') }
        Invoke-Generate
    }

    $splitDone = @(Get-ChildItem (Join-Path $UsbDir 'numbers-*.txt') -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*-summary.txt' }).Count -ge 8
    if ($splitDone -and -not $force) {
        Write-Step 'usb/ already has the split files - skipping split (use --force to redo)'
    }
    else {
        $Rest = [System.Collections.Generic.List[string]]::new()
        if ($force) { $Rest.Add('--force') }
        Invoke-Split
    }

    Invoke-Task1
    Invoke-Hardware
    $Rest = [System.Collections.Generic.List[string]]::new()
    Invoke-Verify
    try {
        $Rest = [System.Collections.Generic.List[string]]::new()
        Invoke-Charts
    }
    catch {
        Write-Host "Charts skipped: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'All local work is done.' -ForegroundColor Green
    Write-Host "Next: copy the 8 files from $UsbDir to the USB drive and run:"
    Write-Host '  scripts\lab.ps1 task2 --dir E:\'
}

function Invoke-Clean {
    $all = Take-Flag $Rest 'all'
    Write-Step 'Cleaning build output (bin/, obj/)'
    foreach ($directory in @((Join-Path $LabDir 'bin'), (Join-Path $LabDir 'obj'))) {
        if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
    }
    if ($all) {
        Write-Step 'Removing generated data (numbers.txt, usb/, charts/, hardware.txt, results.csv)'
        foreach ($path in @($NumbersFile, $UsbDir, (Join-Path $LabDir 'charts'), $HardwareFile, $DefaultCsv)) {
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
    }
    Write-Host 'Clean done.'
}

switch ($Command) {
    'check' { Invoke-Check }
    'status' { Invoke-Status }
    'build' { Build }
    'generate' { Invoke-Generate }
    'split' { Invoke-Split }
    'task1a' { Invoke-TaskCommand 'task1a' }
    'task1b' { Invoke-TaskCommand 'task1b' }
    'task1c' { Invoke-TaskCommand 'task1c' }
    'task1' { $SavedTask1Rest = Get-Leftover $Rest; Invoke-Task1 }
    'task2' { Invoke-Task2 }
    'hardware' { Invoke-Hardware }
    'verify' { Invoke-Verify }
    'charts' { Invoke-Charts }
    'all' { Invoke-All }
    'clean' { Invoke-Clean }
    'help' { Get-Content $MyInvocation.MyCommand.Path | Select-Object -First 34 | Select-Object -Skip 3 | Write-Host }
    default {
        Write-Host "Unknown command: $Command" -ForegroundColor Red
        Get-Content $MyInvocation.MyCommand.Path | Select-Object -First 34 | Select-Object -Skip 3 | Write-Host
        exit 1
    }
}
