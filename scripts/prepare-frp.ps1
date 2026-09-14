$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = '0.71.0'
$expectedHash = '9e5062e3e5cf07e67144a3a4acf175ef6a2486f3605dd6cf288bae34ab39819f'
$cacheRoot = Join-Path $projectRoot 'artifacts/frp-download'
$archive = Join-Path $cacheRoot "frp_${version}_windows_amd64.zip"
$target = Join-Path $projectRoot "app/runtime/frp/$version"
New-Item -ItemType Directory -Force -Path $cacheRoot, $target | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/fatedier/frp/releases/download/v$version/frp_${version}_windows_amd64.zip" -OutFile $archive -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'The frp package checksum does not match the pinned release.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in @('frpc.exe', 'LICENSE')) {
        $entry = $zip.GetEntry("frp_${version}_windows_amd64/$name")
        if ($null -eq $entry) { throw "Required file is missing: $name" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target $name), $true)
    }
} finally { $zip.Dispose() }
Write-Host "frpc $version is ready. No connection credentials are included."
