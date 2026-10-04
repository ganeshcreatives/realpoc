param([Parameter(Mandatory=$true)][string]$Email)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:SCHOOL_CONFIG = Join-Path $projectRoot '.local\api.json'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
& (Join-Path $projectRoot '.tools\dotnet\dotnet.exe') (Join-Path $projectRoot 'server\School.Api\bin\Release\net10.0\School.Api.dll') --grant-staff $Email
if ($LASTEXITCODE -ne 0) { throw 'Staff grant failed.' }
