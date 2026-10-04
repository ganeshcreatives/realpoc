param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$KeyId='school-prod-v1')
$ErrorActionPreference='Stop'
if ($KeyId -notmatch '^[a-zA-Z0-9-]{1,64}$') { throw 'Invalid key identifier.' }
$directory=[System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$identity=[System.Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $directory /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not protect the output directory.' }
function New-Key {
  $bytes=New-Object byte[] 32; $rng=[System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose(); [Convert]::ToBase64String($bytes)
}
$apiFile=Join-Path $directory 'SCHOOL_API_KEYRING.json'; $bffFile=Join-Path $directory 'SCHOOL_BFF_KEYRING.json'
if ((Test-Path -LiteralPath $apiFile) -or (Test-Path -LiteralPath $bffFile)) { throw 'Output exists. Refusing to overwrite keys.' }
$ring=@{activeKeyId=$KeyId;keys=@(@{clientId='sc-bff-main';keyId=$KeyId;secret=(New-Key)});sessionEncryptionKey=''}
$ring | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $apiFile -Encoding utf8
$ring.sessionEncryptionKey=New-Key
$ring | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $bffFile -Encoding utf8
Write-Host 'Keyring files written to the protected directory. Upload their contents privately to Infisical; do not commit or paste them into chat.'
