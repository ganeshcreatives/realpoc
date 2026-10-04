param([switch]$SkipInstall)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) {
    if ($SkipInstall) { throw 'Project-local .NET 10 is missing.' }
    New-Item -ItemType Directory -Force -Path '.tools' | Out-Null
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools\dotnet-install.ps1'
    & '.tools\dotnet-install.ps1' -Channel 10.0 -Architecture x64 -InstallDir '.tools\dotnet' -NoPath
    if (-not (Test-Path -LiteralPath $dotnetPath)) { throw '.NET installation failed.' }
}
New-Item -ItemType Directory -Force -Path '.local','.local\mail','artifacts' | Out-Null
# Restrict development secrets, database, email links and logs to this Windows identity and SYSTEM.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe (Join-Path $projectRoot '.local') /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict local secret directory permissions.' }
function New-Key {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes); $rng.Dispose()
    return [Convert]::ToBase64String($bytes)
}
$bffConfig = Join-Path $projectRoot '.local\bff.json'
$apiConfig = Join-Path $projectRoot '.local\api.json'
if ((Test-Path -LiteralPath $bffConfig) -xor (Test-Path -LiteralPath $apiConfig)) { throw 'One local configuration is missing. Restore both configurations; do not silently replace keys.' }
if (-not (Test-Path -LiteralPath $bffConfig)) {
    $serviceKey = New-Key; $encryptionKey = New-Key
    $common = @{
        PublicOrigin = 'http://localhost:5080'; ApiOrigin = 'http://127.0.0.1:5081'
        Database = @{ Provider = 'Sqlite'; Connection = "Data Source=$(Join-Path $projectRoot '.local\school.db');Default Timeout=3" }
        Mail = @{ Mode = 'File'; Directory = (Join-Path $projectRoot '.local\mail') }
        Logging = @{ LogLevel = @{ Default = 'Information' } }
    }
    foreach ($role in @('api','bff')) {
        $keyring = @{ activeKeyId='school-local-v1'; keys=@(@{clientId='sc-bff-main';keyId='school-local-v1';secret=$serviceKey}); sessionEncryptionKey=$(if($role -eq 'bff'){$encryptionKey}else{''}) } | ConvertTo-Json -Depth 8 -Compress
        $common.Secrets = @{ Provider='Configuration'; KeyRing=$keyring }
        $common | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $projectRoot ".local\$role.json") -Encoding utf8
    }
}
& npm.cmd ci --ignore-scripts
if ($LASTEXITCODE -ne 0) { throw 'npm dependency installation failed.' }
& npm.cmd run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend/SDK build failed.' }
& $dotnetPath build 'server\School.Api\School.Api.csproj' -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
& $dotnetPath build 'server\School.Bff\School.Bff.csproj' -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'BFF build failed.' }
$env:SCHOOL_CONFIG = $apiConfig; $env:ASPNETCORE_ENVIRONMENT = 'Development'
& $dotnetPath 'server\School.Api\bin\Release\net10.0\School.Api.dll' --migrate-db
if ($LASTEXITCODE -ne 0) { throw 'Database initialization failed.' }
& npm.cmd run pack:sdk
if ($LASTEXITCODE -ne 0) { throw 'SDK packaging failed.' }
Write-Host 'Setup complete. Start with .\scripts\Start.ps1. Local email links are in .local\mail.'
