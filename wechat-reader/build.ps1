$ErrorActionPreference='Stop'
$readerRoot=$PSScriptRoot
$readerOutput=Join-Path (Split-Path $readerRoot -Parent) 'app\runtime\wechat-reader'
New-Item -ItemType Directory -Force -Path $readerOutput | Out-Null
Push-Location $readerRoot
$readerOldCgo=$env:CGO_ENABLED
$readerOldOS=$env:GOOS
$readerOldArch=$env:GOARCH
try {
 $env:CGO_ENABLED='0';$env:GOOS='windows';$env:GOARCH='amd64'
 go build -trimpath -ldflags '-s -w' -o (Join-Path $readerOutput 'member-wechat-reader.exe') .
 if($LASTEXITCODE -ne 0){throw '微信读取组件构建失败'}
 Copy-Item -LiteralPath (Join-Path $readerRoot 'LICENSE') -Destination (Join-Path $readerOutput 'LICENSE.chatlog')
 Copy-Item -LiteralPath (Join-Path $readerRoot 'LICENSE.masked-key-patch') -Destination $readerOutput
 Copy-Item -LiteralPath (Join-Path $readerRoot 'UPSTREAM.md') -Destination $readerOutput
 Copy-Item -LiteralPath (Join-Path $readerRoot 'THIRD_PARTY_LICENSES.txt') -Destination $readerOutput
} finally {$env:CGO_ENABLED=$readerOldCgo;$env:GOOS=$readerOldOS;$env:GOARCH=$readerOldArch;Pop-Location}
