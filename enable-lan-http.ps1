# Run once as Administrator to let the C# HTTP broker (HttpListener) listen on
# the LAN, not only on localhost. After this, start the broker with:
#   dotnet run --project Lab4/part1-http/broker -- http://+:8080/
# The Go broker does not need this (it binds 0.0.0.0 directly).
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This script must run as Administrator (right-click PowerShell -> Run as administrator)." -ForegroundColor Yellow
    exit 1
}

$url = "http://+:8080/"
$existing = netsh http show urlacl url=$url 2>$null | Out-String
if ($existing -match [regex]::Escape($url)) {
    Write-Host "URL reservation already present: $url"
} else {
    netsh http add urlacl url=$url user=Everyone | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "netsh http add urlacl failed (exit $LASTEXITCODE)" }
    Write-Host "Added URL reservation: $url (user=Everyone)"
}

$ruleName = "NP-Labs TCP 8080"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -Profile Any | Out-Null
    Write-Host "Allowed TCP 8080 through Windows Firewall"
} else {
    Write-Host "Firewall rule already present: $ruleName"
}

Write-Host ""
Write-Host "Now run the C# broker with a LAN prefix, for example:"
Write-Host "  dotnet run --project Lab4/part1-http/broker -- http://+:8080/"
Write-Host "and point publishers/subscribers at http://<this-pc-ip>:8080/"
