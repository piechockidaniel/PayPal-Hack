[CmdletBinding()]
param(
    [string]$PublicWebhookUrl = 'https://paypal.connect-the-dots.biz/api/webhooks/paypal',
    [int]$UiPort = 64490,
    [int]$WebhookPort = 64491
)

$ErrorActionPreference = 'Stop'

function Assert-Pass {
    param([string]$Message)
    Write-Host "PASS $Message" -ForegroundColor Green
}

function Assert-Fail {
    param([string]$Message)
    throw "FAIL $Message"
}

$required = @(
    'PAYPAL_CLIENT_ID',
    'PAYPAL_CLIENT_SECRET',
    'PAYPAL_WEBHOOK_ID',
    'PAYPAL360_WEBHOOK_HOST'
)
$missing = @($required | Where-Object {
    [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
})
if ($missing.Count -gt 0) {
    Assert-Fail "required environment variables are missing: $($missing -join ', ')"
}
Assert-Pass 'required environment variable names are populated'

foreach ($port in @($UiPort, $WebhookPort)) {
    $listening = Test-NetConnection -ComputerName 127.0.0.1 -Port $port `
        -InformationLevel Quiet -WarningAction SilentlyContinue
    if (-not $listening) {
        Assert-Fail "local port $port is not listening"
    }
    Assert-Pass "local port $port is listening"
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.ServerCertificateCustomValidationCallback = `
    [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
$client = [System.Net.Http.HttpClient]::new($handler)
try {
    $localResponse = $client.GetAsync("http://127.0.0.1:$WebhookPort/").GetAwaiter().GetResult()
    $localStatus = [int]$localResponse.StatusCode
    if ($localStatus -ne 200) {
        Assert-Fail "local UI health returned HTTP $localStatus"
    }
    Assert-Pass 'local UI health returned HTTP 200'

    $content = [System.Net.Http.StringContent]::new(
        '{}', [System.Text.Encoding]::UTF8, 'application/json')
    $publicResponse = $client.PostAsync($PublicWebhookUrl, $content).GetAwaiter().GetResult()
    $publicStatus = [int]$publicResponse.StatusCode
    if ($publicStatus -ne 400) {
        Assert-Fail "public unsigned webhook probe returned HTTP $publicStatus instead of 400"
    }
    Assert-Pass 'public webhook route reached the signature gate (HTTP 400)'
}
finally {
    $client.Dispose()
    $handler.Dispose()
}

$logPath = Join-Path $env:LOCALAPPDATA 'PayPal360\tunnel\supervisor.log'
if (-not (Test-Path -LiteralPath $logPath)) {
    Assert-Fail "tunnel supervisor log was not found at $logPath"
}
$recentLog = Get-Content -LiteralPath $logPath -Tail 40
if (-not ($recentLog -match 'Public webhook health probe passed\.')) {
    Assert-Fail 'tunnel supervisor has no recent successful public health probe'
}
Assert-Pass 'tunnel supervisor has a recent successful public health probe'

Write-Host 'Demo preflight passed.' -ForegroundColor Green
