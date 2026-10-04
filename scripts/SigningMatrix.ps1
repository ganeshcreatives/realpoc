# Local-only, disposable signing/security probes for the current School Portal contract.
# Enforce-mode cases for the current contract. Mode transitions are covered separately.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bff = 'http://localhost:5080'
$api = 'http://127.0.0.1:5081'
if (-not ([Uri]$bff).IsLoopback -or -not ([Uri]$api).IsLoopback) { throw 'Local-only matrix.' }
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(35)
$script:cookie = ''
$script:key = ''
$script:passed = 0
$script:failed = 0
function Hex([byte[]]$bytes) { if ($null -eq $bytes) { $bytes = [byte[]]::new(0) }; [BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData([byte[]]$bytes)).Replace('-','').ToLowerInvariant() }
function Invoke-Probe([string]$method, [string]$path, [string]$body = '', [hashtable]$options = @{}) {
    $target = if ($options.Api) { $api } else { $bff }
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "$target$path")
    # A 413 can close the socket before the body is sent; do not reuse that connection.
    $request.Headers.ConnectionClose = $true
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    if ($body.Length) {
        $request.Content = [System.Net.Http.ByteArrayContent]::new($bytes)
        $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($(if ($options.ContentType) { $options.ContentType } else { 'application/json' }))
    }
    if (-not $options.NoCookie -and $script:cookie -and -not $options.Api) { [void]$request.Headers.TryAddWithoutValidation('Cookie', $script:cookie) }
    if ($method -ne 'GET' -and -not $options.Api) {
        $requestOrigin = if ($options.ContainsKey('Origin')) { [string]$options.Origin } else { $bff }
        if ($requestOrigin) { [void]$request.Headers.TryAddWithoutValidation('Origin', $requestOrigin) }
    }
    if ($options.Sign) {
        $stamp = if ($options.ContainsKey('Stamp')) { [string]$options.Stamp } else { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() }
        $rid = if ($options.ContainsKey('RequestId')) { [string]$options.RequestId } else { [Guid]::NewGuid().ToString() }
        $signedMethod = if ($options.SignMethod) { [string]$options.SignMethod } else { $method }
        $signedPath = if ($options.SignPath) { [string]$options.SignPath } else { $path }
        $signedBody = if ($options.ContainsKey('SignBody')) { [Text.Encoding]::UTF8.GetBytes([string]$options.SignBody) } else { $bytes }
        $signingKey = if ($options.Key) { [string]$options.Key } else { $script:key }
        $canonical = @($signedMethod, $signedPath, $stamp, $rid, 'main', (Hex $signedBody)) -join "`n"
        $hmac = [System.Security.Cryptography.HMACSHA256]::new([Convert]::FromBase64String($signingKey))
        try { $signature = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))) }
        finally { $hmac.Dispose() }
        if ($options.DuplicateSignature) { $signature = "$signature, $signature" }
        [void]$request.Headers.TryAddWithoutValidation('X-SC-App', 'main')
        [void]$request.Headers.TryAddWithoutValidation('X-SC-Session', 'cookie')
        [void]$request.Headers.TryAddWithoutValidation('X-SC-Timestamp', $stamp)
        [void]$request.Headers.TryAddWithoutValidation('X-SC-Request-Id', $rid)
        if (-not $options.RemoveSignature) { [void]$request.Headers.TryAddWithoutValidation('X-SC-Signature', $signature) }
    }
    if ($options.Extra) {
        foreach ($name in $options.Extra.Keys) { [void]$request.Headers.TryAddWithoutValidation($name, [string]$options.Extra[$name]) }
    }
    $response = $null
    try {
        $response = $client.Send($request)
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $value = try { if ($content) { $content | ConvertFrom-Json } } catch { $null }
        $cookies = if ($response.Headers.Contains('Set-Cookie')) { [string[]]@($response.Headers.GetValues('Set-Cookie')) } else { [string[]]@() }
        $time = if ($response.Headers.Contains('X-SC-Server-Time')) { [string]($response.Headers.GetValues('X-SC-Server-Time') | Select-Object -First 1) } else { '' }
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Code = $value.code; Value = $value; Cookie = ($cookies | Select-Object -First 1); ServerTime = $time }
    }
    finally { $request.Dispose(); if ($response) { $response.Dispose() } }
}
function Check([string]$name, $result, [int]$status, [string]$code = '') {
    $ok = $result.Status -eq $status -and (-not $code -or $result.Code -eq $code)
    if ($ok) { $script:passed++; Write-Host "PASS $name" }
    else { $script:failed++; Write-Host "FAIL ${name}: expected $status $code; got $($result.Status) $($result.Code)" }
}
try {
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0,12)
    $email = "matrix-$suffix@example.invalid"
    $password = 'Local-signing-matrix-only-2026!'
    $register = Invoke-Probe POST '/api-proxy/auth/register' ('{"email":"' + $email + '"}')
    if ($register.Status -ne 202) { throw "Registration setup failed: $($register.Status)" }
    $mail = Get-ChildItem -LiteralPath (Join-Path $root '.local\mail') -File |
        Sort-Object LastWriteTime -Descending |
        Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 1) -eq "To: $email" } |
        Select-Object -First 1
    if (-not $mail) { throw 'Local verification mail missing.' }
    $match = [regex]::Match((Get-Content -LiteralPath $mail.FullName -Raw), '/#verify=([0-9a-f]{64})')
    if (-not $match.Success) { throw 'Local verification token missing.' }
    $verifyBody = @{ token = $match.Groups[1].Value; name = 'Matrix Parent'; password = $password } | ConvertTo-Json -Compress
    $verify = Invoke-Probe POST '/api-proxy/auth/verify' $verifyBody
    if ($verify.Status -ne 200) { throw "Verification setup failed: $($verify.Status)" }
    $login = Invoke-Probe POST '/api-proxy/auth/login' (@{email=$email;password=$password} | ConvertTo-Json -Compress)
    if ($login.Status -ne 200 -or -not $login.Value.signingKey) { throw "Login setup failed: $($login.Status)" }
    $script:key = [string]$login.Value.signingKey
    $script:cookie = ([string]$login.Cookie).Split(';')[0]
    Check '01 login returns BFF context' $login 200
    Check '02 cookie is HttpOnly' ([pscustomobject]@{Status=$(if ($login.Cookie -match 'HttpOnly') {200} else {500});Code=''}) 200
    Check '03 cookie is SameSite Strict' ([pscustomobject]@{Status=$(if ($login.Cookie -match 'SameSite=Strict') {200} else {500});Code=''}) 200
    Check '04 server time is present' ([pscustomobject]@{Status=$(if ($login.ServerTime -match '^\d{13}$') {200} else {500});Code=''}) 200
    Check '05 signed GET schools' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true}) 200
    Check '06 signed GET students' (Invoke-Probe GET '/api-proxy/api/students' '' @{Sign=$true}) 200
    $studentId = [Guid]::NewGuid().ToString()
    Check '07 signed POST student' (Invoke-Probe POST '/api-proxy/api/students' (@{id=$studentId;name='Matrix Student';grade=2}|ConvertTo-Json -Compress) @{Sign=$true}) 200
    Check '08 signed GET applications' (Invoke-Probe GET '/api-proxy/api/applications' '' @{Sign=$true}) 200
    $replayId = [Guid]::NewGuid().ToString()
    Check '09 first nonce accepted' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;RequestId=$replayId}) 200
    Check '10 repeated nonce rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;RequestId=$replayId}) 403 'REPLAY_DETECTED'
    Check '11 changed path rejected' (Invoke-Probe GET '/api-proxy/api/students' '' @{Sign=$true;SignPath='/api-proxy/api/schools'}) 403 'SIGNATURE_INVALID'
    Check '12 changed method rejected' (Invoke-Probe POST '/api-proxy/api/students' '{}' @{Sign=$true;SignMethod='GET'}) 403 'SIGNATURE_INVALID'
    Check '13 changed body rejected' (Invoke-Probe POST '/api-proxy/api/students' '{"name":"changed"}' @{Sign=$true;SignBody='{"name":"original"}'}) 403 'SIGNATURE_INVALID'
    Check '14 query is forbidden by contract' (Invoke-Probe GET '/api-proxy/api/schools?x=1' '' @{Sign=$true}) 400 'INVALID_QUERY'
    Check '15 absent signature rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;RemoveSignature=$true}) 403 'SIGNATURE_MISSING'
    Check '16 duplicate signature rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;DuplicateSignature=$true}) 403 'SIGNATURE_DUPLICATED'
    Check '17 old timestamp rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;Stamp=[DateTimeOffset]::UtcNow.AddMinutes(-6).ToUnixTimeMilliseconds().ToString()}) 403 'TIMESTAMP_SKEW'
    Check '18 future timestamp rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;Stamp=[DateTimeOffset]::UtcNow.AddMinutes(6).ToUnixTimeMilliseconds().ToString()}) 403 'TIMESTAMP_SKEW'
    $otherKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    Check '19 wrong browser key rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;Key=$otherKey}) 403 'SIGNATURE_INVALID'
    Check '20 cookie absent' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;NoCookie=$true}) 401 'SESSION_REQUIRED'
    Check '21 bearer alone is insufficient' (Invoke-Probe GET '/api-proxy/api/schools' '' @{NoCookie=$true;Extra=@{Authorization='Bearer stolen'}}) 401 'SESSION_REQUIRED'
    Check '22 foreign Origin rejected' (Invoke-Probe POST '/api-proxy/api/students' '{}' @{Sign=$true;Origin='https://evil.example'}) 403 'CSRF_ORIGIN'
    Check '23 absent Origin rejected' (Invoke-Probe POST '/api-proxy/api/students' '{}' @{Sign=$true;Origin=''}) 403 'CSRF_ORIGIN'
    Check '24 client service headers ignored' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;Extra=@{'X-Key-Id'='forged';'X-Signature'='fake';'X-Client-Id'='evil'}}) 200
    Check '25 unsigned logout rejected' (Invoke-Probe POST '/api-proxy/api/logout' '{}' @{}) 403 'SIGNATURE_MISSING'
    Check '26 TRACE refused' (Invoke-Probe TRACE '/api-proxy/api/schools' '' @{}) 405
    Check '27 multipart refused' (Invoke-Probe POST '/api-proxy/api/students' '--bad--' @{Sign=$true;ContentType='multipart/form-data; boundary=bad'}) 415 'UNSUPPORTED_MEDIA_TYPE'
    Check '28 text body refused' (Invoke-Probe POST '/api-proxy/api/students' 'hello' @{Sign=$true;ContentType='text/plain'}) 415 'UNSUPPORTED_MEDIA_TYPE'
    Check '29 oversized body refused' (Invoke-Probe POST '/api-proxy/api/students' ('x' * 33000) @{}) 413 'PAYLOAD_TOO_LARGE'
    Check '30 overlong request ID rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;RequestId=('r' * 200)}) 403 'REQUEST_ID_INVALID'
    Check '31 malformed request ID rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;RequestId='not-a-uuid'}) 403 'REQUEST_ID_INVALID'
    Check '32 cross-site fetch rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;Extra=@{'Sec-Fetch-Site'='cross-site'}}) 403 'CSRF_ORIGIN'
    Check '33 forwarded spoof does not change signed request' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true;Extra=@{'X-Forwarded-For'='1.2.3.4';'X-HTTP-Method-Override'='DELETE'}}) 200
    Check '34 direct API unsigned denied' (Invoke-Probe GET '/api/schools' '' @{Api=$true}) 403 'SIGNATURE_MISSING'
    $serviceRing = ((Get-Content (Join-Path $root '.local\api.json') -Raw | ConvertFrom-Json).Secrets.KeyRing | ConvertFrom-Json)
    $known = $serviceRing.Keys[0]
    $baseService = @{'X-Client-Id'=$known.ClientId;'X-Key-Id'=$known.KeyId;'X-Signature-Version'='2';'X-Timestamp'=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString();'X-Request-Id'=[Guid]::NewGuid().ToString();'X-Signature'=[Convert]::ToBase64String([byte[]]::new(32))}
    Check '35 direct API forged service HMAC denied' (Invoke-Probe GET '/api/schools' '' @{Api=$true;Extra=$baseService}) 403 'SIGNATURE_INVALID'
    $unknown = $baseService.Clone(); $unknown['X-Key-Id'] = 'unknown-key'
    Check '36 direct API unknown key denied' (Invoke-Probe GET '/api/schools' '' @{Api=$true;Extra=$unknown}) 403 'CLIENT_UNKNOWN'
    Check '37 direct API oversized body refused' (Invoke-Probe POST '/api/students' ('x' * 33000) @{Api=$true}) 413 'PAYLOAD_TOO_LARGE'
    Check '38 signed logout succeeds' (Invoke-Probe POST '/api-proxy/api/logout' '{}' @{Sign=$true}) 204
    Check '39 cookie after logout rejected' (Invoke-Probe GET '/api-proxy/api/schools' '' @{Sign=$true}) 401 'SESSION_EXPIRED'
    Write-Host "$script:passed passed; $script:failed failed"
    if ($script:failed) { exit 1 }
}
finally { $client.Dispose(); $handler.Dispose() }
