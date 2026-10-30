[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'PayPal360\certificates'),
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\PayPal360.csproj'),
    [ValidateRange(1, 10)]
    [int]$YearsValid = 3,
    [switch]$SkipUserSecrets,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
    throw 'New-SelfSignedCertificate is unavailable. Run this script on Windows PowerShell with the certificate cmdlets installed.'
}
if (-not (Get-Command Export-PfxCertificate -ErrorAction SilentlyContinue)) {
    throw 'Export-PfxCertificate is unavailable. Run this script on Windows PowerShell with the certificate cmdlets installed.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI is required to store the certificate settings in User Secrets.'
}

$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$directory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null

$pfxPath = Join-Path $directory 'paypal360-data-protection.pfx'
$cerPath = Join-Path $directory 'paypal360-data-protection.cer'
if (-not $Force -and (Test-Path -LiteralPath $pfxPath)) {
    throw "Certificate already exists at $pfxPath. Use -Force only when you intentionally want to replace it."
}

$passwordBytes = New-Object byte[] 32
$random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($passwordBytes)
}
finally {
    $random.Dispose()
}
$certificatePassword = [Convert]::ToBase64String($passwordBytes)

$certificate = New-SelfSignedCertificate `
    -Subject 'CN=PayPal360 Data Protection' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($YearsValid)

try {
    $securePassword = ConvertTo-SecureString $certificatePassword -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword -Force:$Force | Out-Null
    Export-Certificate -Cert $certificate -FilePath $cerPath -Force:$Force | Out-Null

    if (-not $SkipUserSecrets) {
        & dotnet user-secrets set 'DataProtection:PAYPAL360_DATA_PROTECTION_CERTIFICATE_PATH' $pfxPath --project $project | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet user-secrets failed while storing the certificate path.'
        }
        & dotnet user-secrets set 'DataProtection:PAYPAL360_DATA_PROTECTION_CERTIFICATE_PASSWORD' $certificatePassword --project $project | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet user-secrets failed while storing the certificate password.'
        }
    }
}
catch {
    Remove-Item -LiteralPath $pfxPath, $cerPath -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host 'PayPal360 data-protection certificate created.' -ForegroundColor Green
Write-Host "PFX: $pfxPath"
Write-Host "Certificate: $cerPath"
Write-Host "Thumbprint: $($certificate.Thumbprint)"
if ($SkipUserSecrets) {
    Write-Host 'User Secrets were not changed (-SkipUserSecrets).' -ForegroundColor Yellow
}
else {
    Write-Host 'User Secrets updated without printing the generated password.' -ForegroundColor Green
}
