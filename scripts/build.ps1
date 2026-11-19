# scripts/build.ps1 — 构建 REAArchive（零 NuGet，离线可编）
# 用法：pwsh scripts/build.ps1 [-Publish] [-SelfContained] [-ReadyToRun]
param(
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$ReadyToRun
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\REAArchive\REAArchive.csproj'
if (-not (Test-Path $proj)) { throw "找不到工程：$proj" }

Write-Host "== 构建 REAArchive ==" -ForegroundColor Cyan
& dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

$out = Join-Path $root 'src\REAArchive\bin\Release\net9.0-windows'
Write-Host "`n构建产物：$out\REAArchive.exe" -ForegroundColor Green
Get-ChildItem $out -Filter 'REAArchive.exe' | ForEach-Object { "  {0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB) }

if ($Publish) {
    $pub = Join-Path $root 'dist'
    Write-Host "`n== 发布到 dist（$(if ($SelfContained) { '自包含' } else { '框架依赖' })）==" -ForegroundColor Cyan
    $args = @('publish', $proj, '-c', 'Release', '-o', $pub, '--nologo')
    if ($SelfContained) {
        $args += @('-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    } else {
        $args += @('-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true')
    }
    if ($ReadyToRun) {
        # 预热 JIT：冷启动约省 70–140ms（实测窗口 2.52s → 2.45s）。首次使用需要联网还原 runtime pack。
        $args += '-p:PublishReadyToRun=true'
    }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }
    Write-Host "发布产物：$pub\REAArchive.exe" -ForegroundColor Green
}
