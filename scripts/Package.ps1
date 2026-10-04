param([string]$Destination)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination=Join-Path $projectRoot 'artifacts\School-Portal-Source.zip' }
$absolute=[System.IO.Path]::GetFullPath($Destination)
$parent=[System.IO.Path]::GetDirectoryName($absolute)
New-Item -ItemType Directory -Force -Path $parent | Out-Null
if (Test-Path -LiteralPath $absolute) { throw 'Package exists. Specify a new destination.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folders=@('apps','packages','server','scripts','deploy','docs','config','.github')
$files=New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
foreach($folder in $folders){
    Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Recurse -File | Where-Object {
        $relative=$_.FullName.Substring($projectRoot.Length+1).Replace('\','/')
        $relative -notmatch '(^|/)(node_modules|bin|obj|dist|wwwroot|\.local|\.tools|artifacts|\.git)(/|$)' -and $relative -notmatch '\.(db|log|error\.log|tgz)$'
    } | ForEach-Object { $files.Add($_) }
}
foreach($name in @('README.md','package.json','package-lock.json','Directory.Build.props','global.json','Dockerfile','render.yaml','.dockerignore','.gitignore','.gitattributes')){
    $files.Add((Get-Item -LiteralPath (Join-Path $projectRoot $name)))
}
$archive=[System.IO.Compression.ZipFile]::Open($absolute,[System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($file in $files){
        $relative=$file.FullName.Substring($projectRoot.Length+1).Replace('\','/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[System.IO.Compression.CompressionLevel]::Optimal)|Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "Source package created: $($files.Count) reviewed files."
