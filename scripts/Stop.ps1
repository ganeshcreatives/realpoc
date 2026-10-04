$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$statePath = Join-Path $projectRoot '.local\processes.json'
if (-not (Test-Path -LiteralPath $statePath)) { Write-Host 'No recorded processes.'; return }
$entries = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
foreach ($entry in $entries) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($entry.Id)" -ErrorAction SilentlyContinue
    $expectedExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    $expectedDll = Join-Path $projectRoot "server\School.$($entry.Role)\bin\Release\net10.0\School.$($entry.Role).dll"
    if ($process -and $process.ExecutablePath -eq $expectedExe -and $process.CommandLine.Contains($expectedDll)) { Stop-Process -Id $process.ProcessId }
}
Remove-Item -LiteralPath $statePath
Write-Host 'Recorded School Portal services stopped.'
