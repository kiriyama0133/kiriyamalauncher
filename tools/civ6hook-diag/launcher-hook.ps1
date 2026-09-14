# 把诊断 Hook「装进启动器的自动注入路径」，或者还原成原来的 hookdll.dll。
#
# 背景：启动器检测到文明 6 进程后会跑 Scripts\civ6\injciv6.exe，而注入的就是
#       Scripts\civ6\hookdll.dll。把这个文件换成我们的诊断版，就能在游戏刚启动时
#       就带上 Hook —— 不会漏掉进入「多人游戏 → 局域网」时最早发出的那批广播。
#
# 用法：
#   .\launcher-hook.ps1              # 备份原版 → 换成诊断版
#   .\launcher-hook.ps1 -Restore     # 还原成原版
#   .\launcher-hook.ps1 -Target <目录>   # 手动指定 Scripts\civ6 目录

param(
    [switch]$Restore,
    [string]$Target
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'build\hookdll.dll'
$backupName = 'hookdll.original.dll'

function Find-HookDirectory {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path $Explicit) { return (Get-Item $Explicit).FullName }
        throw "指定的目录不存在：$Explicit"
    }

    $candidates = @(
        (Join-Path $PSScriptRoot '..\..\kiriyamalauncher.Presentation\Scripts\civ6'),
        (Join-Path $PSScriptRoot '..\..\Scripts\civ6'),
        (Join-Path $PSScriptRoot '..\Scripts\civ6')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Get-Item $candidate).FullName
        }
    }

    throw "找不到 Scripts\civ6 目录。用 -Target 手动指定它，例如：`n  .\launcher-hook.ps1 -Target 'F:\KiriyamaLauncher\kiriyamalauncher\kiriyamalauncher.Presentation\Scripts\civ6'"
}

$hookDirectory = Find-HookDirectory -Explicit $Target
$hookDll = Join-Path $hookDirectory 'hookdll.dll'
$hookDll64 = Join-Path $hookDirectory 'hookdll64.dll'
$backup = Join-Path $hookDirectory $backupName

Write-Host "目标目录：$hookDirectory"

$game = Get-Process -Name 'CivilizationVI*' -ErrorAction SilentlyContinue

if ($game) {
    Write-Host "文明 6 还在运行（PID $($game.Id -join ', ')），DLL 会被占用。请先关掉游戏。" -ForegroundColor Yellow
    return
}

if ($Restore) {
    if (-not (Test-Path $backup)) {
        Write-Host "没有找到备份 $backupName，无法还原。" -ForegroundColor Red
        return
    }

    Copy-Item $backup $hookDll -Force
    Write-Host "已还原原版 hookdll.dll。" -ForegroundColor Green
    return
}

if (-not (Test-Path $source)) {
    Write-Host "找不到诊断 DLL（$source）。请先运行 .\build-diag.ps1。" -ForegroundColor Red
    return
}

if (-not (Test-Path $backup)) {
    Copy-Item $hookDll $backup -Force
    Write-Host "已备份原版：$backupName" -ForegroundColor Green
}
else {
    Write-Host "原版备份已存在，保留不动：$backupName" -ForegroundColor DarkGray
}

Copy-Item $source $hookDll -Force
Write-Host "已换成诊断版 hookdll.dll" -ForegroundColor Green

if (Test-Path $hookDll64) {
    Copy-Item $source $hookDll64 -Force
    Write-Host "已换成诊断版 hookdll64.dll" -ForegroundColor Green
}

Write-Host ""
Write-Host "接下来可以直接用启动器启动文明 6（会自动注入诊断 Hook）。" -ForegroundColor Cyan
Write-Host "想还原就运行：.\launcher-hook.ps1 -Restore" -ForegroundColor Cyan
