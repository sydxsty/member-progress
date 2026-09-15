param([string]$Repository = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $Repository 'app\runtime\embedding\bundled.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$destination = Join-Path $Repository 'app\runtime\embedding\bge-m3-fp32'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($item in $manifest.files) {
    $target = Join-Path $destination $item.path
    if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ieq $item.sha256)) { continue }
    $remotePath = if ($item.path -in @('model.onnx','model.onnx_data','Constant_7_attr__value')) { 'onnx/' + $item.path } else { $item.path }
    $uri = 'https://huggingface.co/' + $manifest.model + '/resolve/' + $manifest.revision + '/' + $remotePath
    $partial = $target + '.partial'
    Write-Host ('Downloading ' + $item.path)
    Invoke-WebRequest -Uri $uri -OutFile $partial -UseBasicParsing
    if ((Get-Item -LiteralPath $partial).Length -ne $item.size -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ine $item.sha256) { throw ('Model checksum mismatch: ' + $item.path) }
    Move-Item -LiteralPath $partial -Destination $target -Force
}
Write-Host 'BGE-M3 FP32 is ready; users of the portable package need no model installation.'
