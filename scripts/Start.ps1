$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot '.local\bff.json'))) { throw 'Run scripts\Setup.ps1 first.' }
foreach ($port in @(5080,5081)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port is already in use. Stop the existing process before starting." }
}
$pids = @()
try {
    foreach ($role in @('Api','Bff')) {
        $lower = $role.ToLowerInvariant()
        $env:SCHOOL_CONFIG = Join-Path $projectRoot ".local\$lower.json"
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:ASPNETCORE_URLS = $(if ($role -eq 'Api') { 'http://127.0.0.1:5081' } else { 'http://127.0.0.1:5080' })
        $directory = Join-Path $projectRoot "server\School.$role"
        $dll = Join-Path $directory "bin\Release\net10.0\School.$role.dll"
        $process = Start-Process -FilePath $dotnetPath -ArgumentList "`"$dll`"" -WorkingDirectory $directory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $projectRoot ".local\$lower.log") -RedirectStandardError (Join-Path $projectRoot ".local\$lower.error.log")
        $pids += @{Id=$process.Id;StartedAt=$process.StartTime.ToUniversalTime().ToString('O');Role=$role}
    }
    $pids | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot '.local\processes.json')
    Write-Host 'School Portal is starting at http://localhost:5080. Use scripts\Stop.ps1 to stop it.'
} catch {
    foreach ($entry in $pids) { Stop-Process -Id $entry.Id -ErrorAction SilentlyContinue }
    throw
}
