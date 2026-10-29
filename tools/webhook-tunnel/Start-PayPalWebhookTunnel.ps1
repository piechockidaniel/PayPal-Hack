[CmdletBinding()]
param(
    [string]$RemoteHost = $(if ($env:PAYPAL360_TUNNEL_HOST) { $env:PAYPAL360_TUNNEL_HOST } else { 'connect-the-dots.biz' }),
    [int]$LocalPort = $(if ($env:PAYPAL360_TUNNEL_LOCAL_PORT) { [int]$env:PAYPAL360_TUNNEL_LOCAL_PORT } else { 64491 }),
    [int]$RemotePort = $(if ($env:PAYPAL360_TUNNEL_REMOTE_PORT) { [int]$env:PAYPAL360_TUNNEL_REMOTE_PORT } else { 16491 }),
    [string]$SshUser = $(if ($env:PAYPAL360_TUNNEL_USER) { $env:PAYPAL360_TUNNEL_USER } else { 'root' }),
    [string]$KeyPath = $(if ($env:PAYPAL360_TUNNEL_KEY) { $env:PAYPAL360_TUNNEL_KEY } else { Join-Path $env:USERPROFILE '.ssh\connect-the-dots.ppk' }),
    [string]$PlinkPath = $(if ($env:PAYPAL360_TUNNEL_PLINK) { $env:PAYPAL360_TUNNEL_PLINK } else { 'C:\Program Files\PuTTY\plink.exe' }),
    [string]$HealthUrl = $(if ($env:PAYPAL360_TUNNEL_HEALTH_URL) { $env:PAYPAL360_TUNNEL_HEALTH_URL } else { 'https://paypal.connect-the-dots.biz/api/webhooks/paypal' })
)

$ErrorActionPreference = 'Stop'
$logDirectory = Join-Path $env:LOCALAPPDATA 'PayPal360\tunnel'
$stdoutPath = Join-Path $logDirectory 'plink.stdout.log'
$stderrPath = Join-Path $logDirectory 'plink.stderr.log'
$supervisorLog = Join-Path $logDirectory 'supervisor.log'

New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

function Write-SupervisorLog([string]$Message) {
    Add-Content -LiteralPath $supervisorLog -Value "$(Get-Date -Format o) $Message"
}

function Test-PublicWebhookRoute {
    try {
        $response = Invoke-WebRequest -Uri $HealthUrl -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 10 -ErrorAction Stop
        return $response.StatusCode -in 200, 202, 400, 405
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            return ([int]$_.Exception.Response.StatusCode) -in 400, 405
        }
        return $false
    }
}

if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) {
    throw "SSH key file was not found: $KeyPath"
}
if (-not (Test-Path -LiteralPath $PlinkPath -PathType Leaf)) {
    throw "plink executable was not found: $PlinkPath"
}

$target = "$SshUser@$RemoteHost"
$forward = "127.0.0.1:$RemotePort`:127.0.0.1:$LocalPort"
$arguments = @(
    '-batch',
    '-no-antispoof',
    '-ssh',
    '-N',
    '-T',
    '-R', $forward,
    '-i', $KeyPath,
    $target
)

Write-SupervisorLog "Supervisor started for $target remote port $RemotePort to local port $LocalPort."

while ($true) {
    $localReady = Test-NetConnection -ComputerName 127.0.0.1 -Port $LocalPort -InformationLevel Quiet -WarningAction SilentlyContinue
    if (-not $localReady) {
        Write-SupervisorLog "Local app port $LocalPort is unavailable; waiting."
        Start-Sleep -Seconds 10
        continue
    }

    Write-SupervisorLog "Starting reverse tunnel."
    $plink = Start-Process -FilePath $PlinkPath -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    Start-Sleep -Seconds 3
    if ($plink.HasExited -or -not (Test-PublicWebhookRoute)) {
        Write-SupervisorLog "Public webhook health probe failed; stopping tunnel and retrying."
        Stop-Process -Id $plink.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 10
        continue
    }
    Write-SupervisorLog "Public webhook health probe passed."
    while (-not $plink.HasExited) {
        Start-Sleep -Seconds 5
        if (-not (Test-NetConnection -ComputerName 127.0.0.1 -Port $LocalPort -InformationLevel Quiet -WarningAction SilentlyContinue)) {
            Write-SupervisorLog "Local app disappeared; stopping tunnel process $($plink.Id)."
            Stop-Process -Id $plink.Id -Force -ErrorAction SilentlyContinue
            break
        }
    }

    if ($plink.HasExited) {
        Write-SupervisorLog "Tunnel exited with code $($plink.ExitCode); retrying after backoff."
    }
    Start-Sleep -Seconds 10
}
