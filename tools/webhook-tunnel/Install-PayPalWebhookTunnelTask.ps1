[CmdletBinding()]
param(
    [string]$TaskName = 'PayPal360 Webhook Tunnel'
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Start-PayPalWebhookTunnel.ps1'
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$startupDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$shortcutPath = Join-Path $startupDirectory "$TaskName.lnk"
$arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scriptPath`""

New-Item -ItemType Directory -Force -Path $startupDirectory | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $pwsh
$shortcut.Arguments = $arguments
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.WindowStyle = 7
$shortcut.Description = 'Reconnect PayPal 360 public webhook tunnel'
$shortcut.Save()

Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'pwsh.exe' -and $_.CommandLine -like '*Start-PayPalWebhookTunnel.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'plink.exe' -and $_.CommandLine -like '*-R 127.0.0.1:16491:127.0.0.1:64491*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Process -FilePath $pwsh -ArgumentList $arguments -WindowStyle Hidden
Write-Output "Installed and started per-user Startup launcher: $shortcutPath"
