# Local-only integration check for signature verification modes at both hops.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$base = 'http://localhost:5080'
$api = 'http://127.0.0.1:5081'
$previousBrowser = $env:Verification__Browser
$previousService = $env:Verification__Service
function Call([string]$method,[string]$url,[string]$json = '',$session = $null) {
    $args = @{ Method=$method; Uri=$url; SkipHttpErrorCheck=$true; TimeoutSec=35 }
    if ($json) { $args.Body=$json; $args.ContentType='application/json' }
    if ($method -ne 'GET' -and $url.StartsWith($base)) { $args.Headers=@{Origin=$base} }
    if ($session) { $args.WebSession=$session }
    return Invoke-WebRequest @args
}
function Expect([string]$name,$response,[int]$status) {
    if ([int]$response.StatusCode -ne $status) { throw "$name expected $status but got $([int]$response.StatusCode)" }
    Write-Host "PASS $name"
}
try {
    foreach ($mode in @('Off','Shadow','Enforce')) {
        $env:Verification__Browser=$mode
        $env:Verification__Service=$mode
        & (Join-Path $PSScriptRoot 'Start.ps1')
        try {
            $ready=$false
            for ($attempt=0;$attempt -lt 30;$attempt++) {
                Start-Sleep -Milliseconds 500
                try { $ready=([int](Call GET "$base/health/ready").StatusCode -eq 200) } catch { $ready=$false }
                if ($ready) { break }
            }
            if (-not $ready) { throw "$mode services did not become ready" }
            $email='mode-'+[Guid]::NewGuid().ToString('N')+'@example.invalid'
            Expect "$mode registration" (Call POST "$base/api-proxy/auth/register" (@{email=$email}|ConvertTo-Json -Compress)) 202
            $mail=Get-ChildItem -LiteralPath (Join-Path $root '.local\mail') -File |
                Sort-Object LastWriteTime -Descending |
                Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 1) -eq "To: $email" } |
                Select-Object -First 1
            if (-not $mail) { throw "$mode verification mail missing" }
            $match=[regex]::Match((Get-Content -LiteralPath $mail.FullName -Raw),'/#verify=([0-9a-f]{64})')
            if (-not $match.Success) { throw "$mode verification token missing" }
            $password='Local-mode-check-passphrase-2026!'
            Expect "$mode verification" (Call POST "$base/api-proxy/auth/verify" (@{token=$match.Groups[1].Value;name='Mode Check';password=$password}|ConvertTo-Json -Compress)) 200
            $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
            Expect "$mode login" (Call POST "$base/api-proxy/auth/login" (@{email=$email;password=$password}|ConvertTo-Json -Compress) $session) 200
            $expected=if ($mode -eq 'Enforce') {403} else {200}
            Expect "$mode unsigned browser request" (Call GET "$base/api-proxy/api/schools" '' $session) $expected
            $tamperedHeaders=@{
                Origin=$base; 'X-SC-App'='main'; 'X-SC-Session'='cookie';
                'X-SC-Timestamp'=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString();
                'X-SC-Request-Id'=[Guid]::NewGuid().ToString();
                'X-SC-Signature'=[Convert]::ToBase64String([byte[]]::new(32))
            }
            $tampered=Invoke-WebRequest -Method Post -Uri "$base/api-proxy/api/students" -ContentType 'application/json' -Body (@{id=[Guid]::NewGuid().ToString();name='Disposable Mode Student';grade=2}|ConvertTo-Json -Compress) -Headers $tamperedHeaders -WebSession $session -SkipHttpErrorCheck -TimeoutSec 35
            Expect "$mode forged browser signature" $tampered $expected
            $directExpected=if ($mode -eq 'Enforce') {403} else {202}
            Expect "$mode unsigned direct API auth request" (Call POST "$api/auth/forgot" (@{email=$email}|ConvertTo-Json -Compress)) $directExpected
            $authExpected=if ($mode -eq 'Enforce') {403} else {401}
            Expect "$mode direct API still requires user token" (Call GET "$api/api/schools") $authExpected
            if ($mode -eq 'Shadow') {
                Start-Sleep -Milliseconds 300
                foreach ($entry in @(@{file='bff.log';hop='Browser'},@{file='api.log';hop='Service'})) {
                    $log=Get-Content -LiteralPath (Join-Path $root ".local\$($entry.file)") -Raw
                    if ($log -notmatch "Signature shadow $($entry.hop) SIGNATURE_MISSING") { throw "Shadow log missing for $($entry.hop)" }
                    Write-Host "PASS Shadow safe log for $($entry.hop)"
                }
                $browserLog=Get-Content -LiteralPath (Join-Path $root '.local\bff.log') -Raw
                if ($browserLog -notmatch 'Signature shadow Browser SIGNATURE_INVALID') { throw 'Shadow tamper log missing' }
                Write-Host 'PASS Shadow logs forged browser signature'
            }
        }
        finally { & (Join-Path $PSScriptRoot 'Stop.ps1') }
    }
}
finally {
    $env:Verification__Browser=$previousBrowser
    $env:Verification__Service=$previousService
}
