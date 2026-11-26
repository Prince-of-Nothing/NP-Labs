# Build every NP-Labs project and package the runnable artifacts under dist/.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/build-all.ps1
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path (Join-Path $root "README.md"))) { throw "Run this script from the repository root (scripts/build-all.ps1)." }

$dist = Join-Path $root "dist"
$win = Join-Path $dist "win-x64"
$arm = Join-Path $dist "linux-arm64"
$logs = Join-Path $dist "logs"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $win, $arm, $logs | Out-Null

$failed = New-Object System.Collections.Generic.List[string]

function Publish-Dotnet([string]$name, [string]$project) {
  Write-Host ("== dotnet publish " + $name) -ForegroundColor Cyan
  $out = Join-Path $win $name
  New-Item -ItemType Directory -Force $out | Out-Null
  $log = Join-Path $logs ("dotnet-" + $name + ".log")
  & dotnet publish (Join-Path $root $project) -c Release -o $out --nologo -v q *> $log
  if ($LASTEXITCODE -ne 0) {
    Write-Host ("FAILED dotnet publish " + $name) -ForegroundColor Red
    Get-Content $log -Tail 20 -ErrorAction SilentlyContinue
    $script:failed.Add("dotnet:" + $name)
  }
}

function Build-Go([string]$name, [string]$workdir, [string]$target, [string]$goos, [string]$goarch, [string]$outdir, [string]$exe = "") {
  Write-Host ("== go build " + $name + " (" + $goos + "/" + $goarch + ")") -ForegroundColor Cyan
  $oldGoos = $env:GOOS; $oldGoarch = $env:GOARCH; $oldCgo = $env:CGO_ENABLED
  $env:GOOS = $goos; $env:GOARCH = $goarch; $env:CGO_ENABLED = "0"
  $out = Join-Path $outdir ($name + $exe)
  $log = Join-Path $logs ("go-" + $name + "-" + $goos + "-" + $goarch + ".log")
  Push-Location (Join-Path $root $workdir)
  & go build -o $out $target *> $log
  $code = $LASTEXITCODE
  Pop-Location
  $env:GOOS = $oldGoos; $env:GOARCH = $oldGoarch; $env:CGO_ENABLED = $oldCgo
  if ($code -ne 0) {
    Write-Host ("FAILED go build " + $name) -ForegroundColor Red
    Get-Content $log -Tail 20 -ErrorAction SilentlyContinue
    $script:failed.Add("go:" + $name + ":" + $goos + "/" + $goarch)
  }
}

function Build-Rust([string]$name, [string]$workdir, [string]$binary) {
  if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    Write-Host ("== skip rust " + $name + " (cargo not installed)") -ForegroundColor Yellow
    return
  }
  Write-Host ("== cargo build " + $name) -ForegroundColor Cyan
  $log = Join-Path $logs ("rust-" + $name + ".log")
  Push-Location (Join-Path $root $workdir)
  & cargo build --release *> $log
  $code = $LASTEXITCODE
  Pop-Location
  if ($code -ne 0) {
    Write-Host ("WARNING cargo build failed for " + $name + " (Rust is optional)") -ForegroundColor Yellow
    Get-Content $log -Tail 20 -ErrorAction SilentlyContinue
    return
  }
  $src = Join-Path (Join-Path $root $workdir) ("target\release\" + $binary + ".exe")
  if (-not (Test-Path $src)) {
    $src = Join-Path (Join-Path $root $workdir) ("target\release\" + $binary)
  }
  if (Test-Path $src) {
    Copy-Item $src (Join-Path $win ($name + ".exe")) -Force
  }
}

Write-Host "NP-Labs build-all" -ForegroundColor Green

Publish-Dotnet "lab1" "Lab1/Lab1.csproj"
Publish-Dotnet "lab2-server-cs" "Lab2/csharp/Server/Server.csproj"
Publish-Dotnet "lab2-client-cs" "Lab2/csharp/Client/Client.csproj"
Publish-Dotnet "lab3-chat-cs" "Lab3/csharp/Chat.csproj"
Publish-Dotnet "lab4-http-broker-cs" "Lab4/part1-http/broker/Broker.csproj"
Publish-Dotnet "lab4-http-publisher-cs" "Lab4/part1-http/publisher/Publisher.csproj"
Publish-Dotnet "lab4-http-subscriber-cs" "Lab4/part1-http/subscriber/Subscriber.csproj"
Publish-Dotnet "lab4-grpc-broker-cs" "Lab4/part2-grpc/broker/Broker.csproj"
Publish-Dotnet "lab4-grpc-publisher-cs" "Lab4/part2-grpc/publisher/Publisher.csproj"
Publish-Dotnet "lab4-grpc-subscriber-cs" "Lab4/part2-grpc/subscriber/Subscriber.csproj"

$goTargets = @(
  @{ name = "lab2-server-go";   dir = "Lab2/go/server";        target = "." },
  @{ name = "lab2-client-go";   dir = "Lab2/go/client";        target = "." },
  @{ name = "lab3-chat-go";     dir = "Lab3/go";               target = "." },
  @{ name = "lab4-http-broker-go";      dir = "Lab4/part1-http/go"; target = "./broker" },
  @{ name = "lab4-http-publisher-go";   dir = "Lab4/part1-http/go"; target = "./publisher" },
  @{ name = "lab4-http-subscriber-go";  dir = "Lab4/part1-http/go"; target = "./subscriber" },
  @{ name = "lab4-grpc-broker-go";      dir = "Lab4/part2-grpc/go"; target = "./broker" },
  @{ name = "lab4-grpc-publisher-go";   dir = "Lab4/part2-grpc/go"; target = "./publisher" },
  @{ name = "lab4-grpc-subscriber-go";  dir = "Lab4/part2-grpc/go"; target = "./subscriber" }
)

foreach ($t in $goTargets) {
  Build-Go $t.name $t.dir $t.target "windows" "amd64" $win ".exe"
}
foreach ($t in $goTargets) {
  Build-Go $t.name $t.dir $t.target "linux" "arm64" $arm ""
}

Build-Rust "lab2-server-rust" "Lab2/rust/server" "server"
Build-Rust "lab2-client-rust" "Lab2/rust/client" "client"

$py = Get-Command python -ErrorAction SilentlyContinue
if ($py) {
  Write-Host "== python report script" -ForegroundColor Cyan
  & python -m py_compile (Join-Path $root "Lab2/scripts/metrics_report.py")
  if ($LASTEXITCODE -ne 0) { $failed.Add("python:metrics_report") }
} else {
  Write-Host "== skip python check (python not installed)" -ForegroundColor Yellow
}

$readme = @"
NP-Labs compiled artifacts
==========================

win-x64/       Windows programs (C# publish output, Go/Rust binaries)
linux-arm64/   Go arm64 binaries for the Android phone
logs/          build logs

Lab 2:
  win-x64/lab2-server-cs/Server.exe 5000
  win-x64/lab2-client-cs/Client.exe 127.0.0.1 5000 client
  win-x64/lab2-server-go.exe 5000
  linux-arm64/lab2-client-go 192.168.1.33 5000 client

Lab 3:
  win-x64/lab3-chat-cs/Chat.exe Alice 9000 secret
  win-x64/lab3-chat-go.exe Alice 9000 secret
  linux-arm64/lab3-chat-go Phone 9000 secret

Lab 4 HTTP:
  win-x64/lab4-http-broker-cs/Broker.exe http://localhost:8080/
  win-x64/lab4-http-subscriber-cs/Subscriber.exe http://localhost:8080 grades
  win-x64/lab4-http-publisher-cs/Publisher.exe http://localhost:8080 grades teacher tok-teacher

Lab 4 gRPC:
  win-x64/lab4-grpc-broker-cs/Broker.exe
  win-x64/lab4-grpc-subscriber-cs/Subscriber.exe http://localhost:5000 grades
  win-x64/lab4-grpc-publisher-cs/Publisher.exe http://localhost:5000 grades teacher tok-teacher

Token file (ASCII, in the broker working directory):
  tok-teacher teacher grades,news
  tok-phone phone grades

See docs/tutorials/01-launch.md and docs/tutorials/02-testing.md.
"@
Set-Content -Path (Join-Path $dist "README.txt") -Value $readme -Encoding UTF8

Write-Host "== checksums" -ForegroundColor Cyan
$manifest = Join-Path $dist "SHA256SUMS.txt"
$lines = Get-ChildItem $dist -Recurse -File | Where-Object { $_.FullName -ne $manifest } | Sort-Object FullName | ForEach-Object {
  $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
  $rel = $_.FullName.Substring($dist.Length + 1).Replace("\", "/")
  $hash + "  " + $rel
}
Set-Content -Path $manifest -Value $lines -Encoding ASCII

Write-Host ""
Write-Host "Artifacts:" -ForegroundColor Green
Get-ChildItem $win -File | Sort-Object Name | Select-Object Name, Length | Format-Table -AutoSize | Out-String -Width 140 | Write-Host
Write-Host ("linux/arm64 files: " + (Get-ChildItem $arm -File | Measure-Object).Count)
if ($failed.Count -gt 0) {
  Write-Host ("FAILED steps: " + ($failed -join ", ")) -ForegroundColor Red
  exit 1
}
Write-Host "All builds succeeded." -ForegroundColor Green
