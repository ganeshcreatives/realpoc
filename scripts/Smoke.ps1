# Disposable local integration exercise. Never targets a non-loopback service.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$origin='http://localhost:5080'
$agent=[System.Net.Http.HttpClientHandler]::new();$agent.UseCookies=$true;$agent.CookieContainer=[System.Net.CookieContainer]::new()
$http=[System.Net.Http.HttpClient]::new($agent);$http.BaseAddress=[Uri]$origin
$http.Timeout=[TimeSpan]::FromSeconds(15)
function Hex([byte[]]$bytes) { [BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant() }
function Call([string]$method,[string]$path,[object]$value=$null,[string]$key='',[string]$id='',[switch]$tamper) {
  $wire=if($null -eq $value){''}else{ConvertTo-Json -InputObject $value -Depth 7 -Compress}
  $bytes=[System.Text.Encoding]::UTF8.GetBytes($wire)
  $request=[System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method),$path)
  if($method -ne 'GET') { $request.Headers.TryAddWithoutValidation('Origin',$origin)|Out-Null }
  if($wire.Length) { $request.Content=[System.Net.Http.ByteArrayContent]::new($bytes);$request.Content.Headers.ContentType=[System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/json') }
  if($key) {
    $stamp=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
    if(-not $id){$id=[Guid]::NewGuid().ToString()}
    $hash=Hex ([System.Security.Cryptography.SHA256]::HashData($bytes))
    $canonical=@($method,$path,$stamp,$id,'main',$hash) -join "`n"
    $hmac=[System.Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($key))
    $sig=[Convert]::ToBase64String($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical)));$hmac.Dispose()
    if($tamper){$sig=[Convert]::ToBase64String((New-Object byte[] 32))}
    $request.Headers.TryAddWithoutValidation('X-SC-App','main')|Out-Null
    $request.Headers.TryAddWithoutValidation('X-SC-Session','cookie')|Out-Null
    $request.Headers.TryAddWithoutValidation('X-SC-Timestamp',$stamp)|Out-Null
    $request.Headers.TryAddWithoutValidation('X-SC-Request-Id',$id)|Out-Null
    $request.Headers.TryAddWithoutValidation('X-SC-Signature',$sig)|Out-Null
  }
  try {
    $response=$http.Send($request)
    $body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return @{Status=[int]$response.StatusCode;Value=$(if($body){$body|ConvertFrom-Json}else{$null})}
  } finally { $request.Dispose(); if($response){$response.Dispose()} }
}
function Expect([object]$result,[int]$status,[string]$code='') {
  if($result.Status -ne $status -or ($code -and $result.Value.code -ne $code)) { throw "Expected $status $code, got $($result.Status) $($result.Value.code)" }
}
function Verify([string]$email) {
  $mailDir=Join-Path $projectRoot '.local\mail'
  $mail=Get-ChildItem -LiteralPath $mailDir -File | Sort-Object LastWriteTime -Descending | Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 1) -eq "To: $email" } | Select-Object -First 1
  if(-not $mail){throw 'Local verification message missing.'}
  $contents=Get-Content -LiteralPath $mail.FullName -Raw
  $match=[regex]::Match($contents,'/#verify=([0-9a-f]{64})')
  if(-not $match.Success){throw 'Verification link missing.'}
  Expect (Call POST '/api-proxy/auth/verify' @{token=$match.Groups[1].Value;name='Sample User';password=$passphrase}) 200
}
try {
  if(-not ([Uri]$http.BaseAddress).IsLoopback){throw 'Smoke exercise is local-only.'}
  $suffix=[Guid]::NewGuid().ToString('N').Substring(0,12)
  $passphrase='Local-school-integration-only-2026!'
  $parentEmail="parent-$suffix@example.invalid";$otherEmail="other-$suffix@example.invalid";$staffEmail="staff-$suffix@example.invalid"
  foreach($mail in @($parentEmail,$otherEmail,$staffEmail)){
    Expect (Call POST '/api-proxy/auth/register' @{email=$mail}) 202
    Verify $mail
  }
  $login=Call POST '/api-proxy/auth/login' @{email=$parentEmail;password=$passphrase};Expect $login 200
  $key=$login.Value.signingKey;$parentId=$login.Value.user.id
  $studentId=[Guid]::NewGuid().ToString()
  $student=Call POST '/api-proxy/api/students' @{id=$studentId;name='Sample Student';grade=5} $key;Expect $student 200
  $balance=Call GET "/api-proxy/api/students/$studentId/balance" $null $key;Expect $balance 200
  $school=Call GET '/api-proxy/api/schools' $null $key;Expect $school 200
  $submission=[Guid]::NewGuid().ToString()
  $payload=@{submissionId=$submission;studentId=$studentId;schoolId='oakwood';academicYear='2026-2027'}
  Expect (Call POST '/api-proxy/api/applications' $payload $key) 200
  Expect (Call GET '/api-proxy/api/students' $null $key -tamper) 403 'SIGNATURE_INVALID'
  $replayId=[Guid]::NewGuid().ToString()
  Expect (Call GET '/api-proxy/api/students' $null $key $replayId) 200
  Expect (Call GET '/api-proxy/api/students' $null $key $replayId) 403 'REPLAY_DETECTED'
  Expect (Call POST '/api-proxy/api/logout' @{} $key) 204
  $otherLogin=Call POST '/api-proxy/auth/login' @{email=$otherEmail;password=$passphrase};Expect $otherLogin 200
  $otherKey=$otherLogin.Value.signingKey
  Expect (Call GET "/api-proxy/api/students/$studentId/balance" $null $otherKey) 404 'RECORD_NOT_FOUND'
  Expect (Call POST '/api-proxy/api/applications' @{submissionId=[Guid]::NewGuid().ToString();studentId=$studentId;schoolId='oakwood';academicYear='2026-2027'} $otherKey) 404 'RECORD_NOT_FOUND'
  Expect (Call GET '/api-proxy/api/staff/applications' $null $otherKey) 403 'ACCESS_DENIED'
  Expect (Call POST '/api-proxy/api/logout' @{} $otherKey) 204
  $env:SCHOOL_CONFIG=Join-Path $projectRoot '.local\api.json';$env:ASPNETCORE_ENVIRONMENT='Development'
  & (Join-Path $projectRoot '.tools\dotnet\dotnet.exe') (Join-Path $projectRoot 'server\School.Api\bin\Release\net10.0\School.Api.dll') --grant-staff $staffEmail | Out-Null
  if($LASTEXITCODE -ne 0){throw 'Staff grant failed.'}
  $staffLogin=Call POST '/api-proxy/auth/login' @{email=$staffEmail;password=$passphrase};Expect $staffLogin 200
  $staffKey=$staffLogin.Value.signingKey
  $queue=Call GET '/api-proxy/api/staff/applications' $null $staffKey;Expect $queue 200
  $item=$queue.Value|Where-Object{$_.id -eq $submission -and $_.userId -eq $parentId}|Select-Object -First 1
  if(-not $item){throw 'Submitted application missing from staff queue.'}
  Expect (Call POST "/api-proxy/api/staff/applications/$parentId/$submission/decision" @{status='Approved';version=$item.version} $staffKey) 200
  Expect (Call POST "/api-proxy/api/staff/applications/$parentId/$submission/decision" @{status='Declined';version=$item.version} $staffKey) 409 'VERSION_CONFLICT'
  Write-Host 'Local integration checks passed: registration, verification, login, balance, application, tamper, replay, ownership, staff review and version conflict.'
} finally { $http.Dispose();$agent.Dispose() }
