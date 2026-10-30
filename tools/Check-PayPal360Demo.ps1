[CmdletBinding()]
param(
    [string]$PublicWebhookUrl = 'https://paypal.connect-the-dots.biz/api/webhooks/paypal',
    [int]$UiPort = 64490,
    [int]$WebhookPort = 64491
)

$ErrorActionPreference = 'Stop'

$projectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\PayPal360.csproj')).Path
$secretValues = @{}
$placeholderValues = @('ChangeMe', 'your_openai_api_key', 'your_openai_model')
function Test-ConfiguredValue {
    param([string]$Value)
    return -not [string]::IsNullOrWhiteSpace($Value) -and $placeholderValues -notcontains $Value.Trim()
}
try {
    $secretLines = @(dotnet user-secrets list --project $projectPath 2>$null)
    foreach ($line in $secretLines) {
        $separator = $line.IndexOf('=')
        if ($separator -lt 1) {
            continue
        }
        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ($key) {
            $secretValues[$key] = $value
        }
    }
}
catch {
    # Environment fallback remains available for deployment-style runs.
}

function Get-ConfiguredValue {
    param(
        [string]$SecretKey,
        [string]$EnvironmentKey
    )

    if ($secretValues.ContainsKey($SecretKey) -and (Test-ConfiguredValue $secretValues[$SecretKey])) {
        return $secretValues[$SecretKey]
    }
    $environmentValue = [Environment]::GetEnvironmentVariable($EnvironmentKey)
    if (Test-ConfiguredValue $environmentValue) {
        return $environmentValue
    }
    $scopedEnvironmentValue = [Environment]::GetEnvironmentVariable($SecretKey.Replace(':', '__'))
    if (Test-ConfiguredValue $scopedEnvironmentValue) {
        return $scopedEnvironmentValue
    }
    return $null
}

function Assert-Pass {
    param([string]$Message)
    Write-Host "PASS $Message" -ForegroundColor Green
}

function Assert-Fail {
    param([string]$Message)
    throw "FAIL $Message"
}

$required = @(
    @{ Secret = 'PayPal:PAYPAL_CLIENT_ID'; Environment = 'PAYPAL_CLIENT_ID' },
    @{ Secret = 'PayPal:PAYPAL_CLIENT_SECRET'; Environment = 'PAYPAL_CLIENT_SECRET' },
    @{ Secret = 'PayPal:PAYPAL_WEBHOOK_ID'; Environment = 'PAYPAL_WEBHOOK_ID' },
    @{ Secret = 'PayPal:PAYPAL360_WEBHOOK_HOST'; Environment = 'PAYPAL360_WEBHOOK_HOST' }
)
$missing = @($required | Where-Object { [string]::IsNullOrWhiteSpace((Get-ConfiguredValue $_.Secret $_.Environment)) } | ForEach-Object { $_.Secret })
if ($missing.Count -gt 0) {
    Assert-Fail "required PayPal settings are missing from .NET User Secrets or the deployment environment: $($missing -join ', ')"
}
Assert-Pass 'required PayPal settings are populated from .NET User Secrets or the deployment environment'

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
