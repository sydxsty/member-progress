param([string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'app\runtime\wechat-reader'))
$ErrorActionPreference='Stop'
$ffmpegRelease=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ffmpeg-release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$ffmpegTarget=Join-Path $OutputDirectory 'ffmpeg.exe'
if((Test-Path -LiteralPath $ffmpegTarget) -and (Get-FileHash -LiteralPath $ffmpegTarget -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ffmpegRelease.sha256) {
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg-LICENSE.txt') -Destination (Join-Path $OutputDirectory 'FFmpeg-LICENSE.txt') -Force
 return
}
$ffmpegTemporary=Join-Path ([IO.Path]::GetTempPath()) ('member-wechat-ffmpeg-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $ffmpegTemporary | Out-Null
try {
 $ffmpegArchive=Join-Path $ffmpegTemporary 'ffmpeg.zip'
 Invoke-WebRequest -UseBasicParsing -Uri $ffmpegRelease.downloadUrl -OutFile $ffmpegArchive
 if((Get-FileHash -LiteralPath $ffmpegArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ffmpegRelease.archiveSha256){throw 'FFmpeg 下载包校验失败'}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $ffmpegZip=[IO.Compression.ZipFile]::OpenRead($ffmpegArchive)
 try {
  $ffmpegEntry=@($ffmpegZip.Entries|Where-Object{$_.FullName.EndsWith('/bin/ffmpeg.exe')})
  if($ffmpegEntry.Count -ne 1){throw 'FFmpeg 下载包内容不正确'}
  $ffmpegStaged=Join-Path $ffmpegTemporary 'ffmpeg.exe'
  [IO.Compression.ZipFileExtensions]::ExtractToFile($ffmpegEntry[0],$ffmpegStaged,$false)
 } finally {$ffmpegZip.Dispose()}
 if((Get-FileHash -LiteralPath $ffmpegStaged -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ffmpegRelease.sha256){throw 'FFmpeg 程序校验失败'}
 Copy-Item -LiteralPath $ffmpegStaged -Destination $ffmpegTarget -Force
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg-LICENSE.txt') -Destination (Join-Path $OutputDirectory 'FFmpeg-LICENSE.txt') -Force
} finally {if(Test-Path -LiteralPath $ffmpegTemporary){Remove-Item -LiteralPath $ffmpegTemporary -Recurse -Force}}
