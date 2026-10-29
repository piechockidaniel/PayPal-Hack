[CmdletBinding()]
param(
    [string]$TaskName = 'PayPal360 Webhook Tunnel'
)

$ErrorActionPreference = 'Stop'
$startupDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$shortcutPath = Join-Path $startupDirectory "$TaskName.lnk"

Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'pwsh.exe' -and $_.CommandLine -like '*Start-PayPalWebhookTunnel.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'plink.exe' -and $_.CommandLine -like '*-R 127.0.0.1:16491:127.0.0.1:64491*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Write-Output "Removed per-user Startup launcher: $shortcutPath"
