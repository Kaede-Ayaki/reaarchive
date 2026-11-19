# scripts/pack.ps1 — 一键打包并安装 REAArchive
#   1) 发布单文件自包含 exe（内嵌 7-Zip 引擎，目标机不需要 .NET）
#   2) 安装到 %LOCALAPPDATA%\Programs\REAArchive（当前用户，无需管理员）
#   3) 创建桌面 + 开始菜单快捷方式（一键启动）
#   4) 重装右键菜单，指向安装后的 exe
#
# 用法：
#   pwsh scripts/pack.ps1                 # 全流程
#   pwsh scripts/pack.ps1 -SkipBuild      # 用已有 dist\REAArchive.exe 重新部署
#   pwsh scripts/pack.ps1 -NoShellMenu    # 不动右键菜单
param(
    [switch]$SkipBuild,
    [switch]$NoShellMenu
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\REAArchive\REAArchive.csproj'
$dist = Join-Path $root 'dist'
$exeName = 'REAArchive.exe'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\REAArchive'
$installedExe = Join-Path $installDir $exeName

function Say([string]$m, [string]$c = 'Cyan') { Write-Host $m -ForegroundColor $c }

# ---- 1) 发布 ----
if (-not $SkipBuild) {
    Say "== 1/4 发布单文件自包含 exe =="
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    $args = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:PublishReadyToRun=true',
        '-o', $dist, '--nologo')
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }
    Remove-Item (Join-Path $dist '*.pdb') -Force -ErrorAction SilentlyContinue
} else {
    Say "== 1/4 跳过发布（沿用 dist）=="
}
$srcExe = Join-Path $dist $exeName
if (-not (Test-Path $srcExe)) { throw "找不到 $srcExe，请先运行不带 -SkipBuild 的打包。" }
$srcMb = [math]::Round((Get-Item $srcExe).Length / 1MB, 1)
Say ("   dist: {0} ({1} MB)" -f $srcExe, $srcMb) 'Green'

# ---- 2) 安装（先停掉正在运行的实例，避免 exe 被锁）----
Say "== 2/4 安装到 $installDir =="
Get-Process REAArchive -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item $srcExe $installedExe -Force
Say ("   已安装: {0}" -f $installedExe) 'Green'

# ---- 3) 快捷方式（桌面 + 开始菜单）----
Say "== 3/4 创建快捷方式 =="
$shell = New-Object -ComObject WScript.Shell
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'REAArchive.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'REAArchive.lnk')
)
foreach ($lnkPath in $targets) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $installedExe
    $lnk.WorkingDirectory = $installDir
    $lnk.IconLocation = "$installedExe,0"
    $lnk.Description = 'REAArchive — 纯净压缩（不联网、不检查更新、无广告）'
    $lnk.WindowStyle = 1
    $lnk.Save()
    Say ("   {0}" -f $lnkPath) 'Green'
}

# ---- 4) 右键菜单重新指向安装后的 exe ----
if (-not $NoShellMenu) {
    Say "== 4/4 重装右键菜单 =="
    & $installedExe --uninstall-shell *> $null
    $p = Start-Process -FilePath $installedExe -ArgumentList @('--install-shell') -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput "$env:TEMP\rea-pack-shell.out" -RedirectStandardError "$env:TEMP\rea-pack-shell.err"
    Get-Content "$env:TEMP\rea-pack-shell.out" -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -First 3
    if ($p.ExitCode -ne 0) { throw "右键菜单安装失败（exit $($p.ExitCode)），见 $env:TEMP\rea-pack-shell.err" }
    Say "   右键菜单已指向 $installedExe" 'Green'
} else {
    Say "== 4/4 跳过右键菜单 =="
}

# ---- 摘要 ----
$hash = (Get-FileHash $installedExe -Algorithm SHA256).Hash
Say "`n==== 打包完成 ====" 'Green'
Say ("  安装路径 : {0}" -f $installedExe)
Say ("  大小     : {0} MB" -f ([math]::Round((Get-Item $installedExe).Length / 1MB, 1)))
Say ("  SHA256   : {0}" -f $hash)
Say  "  桌面/开始菜单快捷方式已创建；右键菜单已安装（可 --uninstall-shell 撤销）。"
