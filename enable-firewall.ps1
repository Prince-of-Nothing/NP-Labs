# Run as Administrator to pre-allow NP-Labs ports so Windows Firewall
# never prompts for public/private networks.
$ports = @(5000, 8080, 9000, 9001, 9002, 9003, 9004, 9005, 9006, 9007, 9008, 9009, 9010, 9011, 9012, 9013, 9014)
foreach ($p in $ports) {
    $name = "NP-Labs TCP/UDP $p"
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Protocol Any -LocalPort $p -Action Allow -Profile Any | Out-Null
        Write-Host "Allowed port $p"
    } else {
        Write-Host "Already allowed: $p"
    }
}
Write-Host "Done. Run labs without firewall prompts."