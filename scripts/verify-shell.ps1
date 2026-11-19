# scripts/verify-shell.ps1 — 独立验证 REAArchive 右键菜单集成
# 用法：pwsh scripts/verify-shell.ps1 [-Install] [-Uninstall] [-RunCommands]
# 只读检查默认；-Install/-Uninstall 会写/删 HKCU（可逆）。
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$RunCommands
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\REAArchive\bin\Release\net9.0-windows\REAArchive.exe'
if (-not (Test-Path $exe)) { throw "先构建：pwsh scripts/build.ps1" }

function Run([string[]]$argv) {
    $p = Start-Process -FilePath $exe -ArgumentList $argv -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput "$env:TEMP\vs.out" -RedirectStandardError "$env:TEMP\vs.err"
    return [pscustomobject]@{ Exit = $p.ExitCode; Out = (Get-Content "$env:TEMP\vs.out" -Raw -ErrorAction SilentlyContinue); Err = (Get-Content "$env:TEMP\vs.err" -Raw -ErrorAction SilentlyContinue) }
}

if ($Install) { $r = Run @('--install-shell'); "install: exit=$($r.Exit)"; $r.Out; $r.Err }
if ($Uninstall) { $r = Run @('--uninstall-shell'); "uninstall: exit=$($r.Exit)"; $r.Out; $r.Err }

# ---- 期望的注册表键 ----
$exts = '.7z', '.zip', '.rar', '.tar', '.gz', '.tgz', '.bz2', '.xz', '.iso', '.wim'
$expect = @()
foreach ($e in $exts) { $expect += "HKCU:\Software\Classes\SystemFileAssociations\$e\shell\REAArchive" }
$expect += 'HKCU:\Software\Classes\*\shell\REAArchive'
$expect += 'HKCU:\Software\Classes\Directory\shell\REAArchive'
$expect += 'HKCU:\Software\Classes\Directory\Background\shell\REAArchive'

Write-Host "`n=== 菜单主键 ===" -ForegroundColor Cyan
$present = 0
foreach ($k in $expect) {
    $ok = Test-Path $k
    if ($ok) { $present++ }
    "{0}  {1}" -f $(if ($ok) { '[OK]  ' } else { '[MISS]' }), $k.Replace('HKCU:\Software\Classes\', '')
}
"合计：{0}/{1}" -f $present, $expect.Count

Write-Host "`n=== .7z 子菜单展开 ===" -ForegroundColor Cyan
$parent = 'HKCU:\Software\Classes\SystemFileAssociations\.7z\shell\REAArchive'
if (Test-Path $parent) {
    $p = Get-ItemProperty $parent
    "MUIVerb      = $($p.MUIVerb)"
    "SubCommands  = $($p.SubCommands)   （空=级联）"
    "MultiSelect  = $($p.MultiSelectModel)"
    Get-ChildItem "$parent\shell" -ErrorAction SilentlyContinue | ForEach-Object {
        $cmd = (Get-ItemProperty "$($_.PSPath)\command" -ErrorAction SilentlyContinue).'(default)'
        "  {0,-16} {1}" -f $_.PSChildName, $cmd
    }
}

Write-Host "`n=== 非菜单残留检查（卸载后应为 0）===" -ForegroundColor Cyan
$resid = @()
foreach ($base in 'HKCU:\Software\Classes\SystemFileAssociations', 'HKCU:\Software\Classes\*', 'HKCU:\Software\Classes\Directory', 'HKCU:\Software\Classes\Directory\Background') {
    if (Test-Path "$base\shell") { $resid += Get-ChildItem "$base\shell" -ErrorAction SilentlyContinue | Where-Object PSChildName -match 'REAArchive' }
}
"REAArchive 相关键：{0} 个" -f @($resid).Count

if ($RunCommands) {
    Write-Host "`n=== 模拟 shell 命令（--extract-here / --extract-to / --test）===" -ForegroundColor Cyan
    $work = Join-Path $env:TEMP ('rea-shell-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $src = Join-Path $work 'src'; New-Item -ItemType Directory -Path $src -Force | Out-Null
    Set-Content (Join-Path $src 'a.txt') 'hello' -Encoding UTF8
    Set-Content (Join-Path $src '中文.txt') '中文' -Encoding UTF8
    $arch = Join-Path $work 'sample.7z'
    & (Join-Path $root 'src\REAArchive\Resources\7z\7z.exe') a -t7z $arch (Join-Path $src 'a.txt') (Join-Path $src '中文.txt') | Out-Null
    # extract-here：解压到归档所在目录
    $here = Join-Path $work 'here'; New-Item -ItemType Directory $here -Force | Out-Null
    $a2 = Join-Path $here 'sample.7z'; Copy-Item $arch $a2 -Force
    $r = Run @('--extract-here', $a2); "extract-here exit=$($r.Exit)"; $r.Out; $r.Err
    "  落盘：{0}" -f ((Get-ChildItem $here -File -Exclude '*.7z' | Select-Object -ExpandProperty Name) -join ', ')
    # extract-to：生成同名文件夹
    $to = Join-Path $work 'to'; New-Item -ItemType Directory $to -Force | Out-Null
    $a3 = Join-Path $to 'sample.7z'; Copy-Item $arch $a3 -Force
    $r = Run @('--extract-to', $a3); "extract-to exit=$($r.Exit)"; $r.Out; $r.Err
    $dir = Get-ChildItem $to -Directory | Select-Object -First 1
    "  生成目录：{0}  内容：{1}" -f $dir.Name, ((Get-ChildItem $dir.FullName -File | Select-Object -ExpandProperty Name) -join ', ')
    # test
    $r = Run @('--test', $arch); "test exit=$($r.Exit)"; $r.Out; $r.Err
    Write-Host "  工作目录：$work" -ForegroundColor DarkGray
}
