# 把某个版本的 hookdll.dll 装进启动器的自动注入路径（Scripts\civ6）。
#
# 三种模式：
#   .\deploy-hook.ps1            装「正式版转发 Hook」（build\hookdll.dll）
#   .\deploy-hook.ps1 -Diag      装「诊断版 Hook」（..\civ6hook-diag\build\hookdll.dll，只记录不转发）
#   .\deploy-hook.ps1 -Restore   还原成最初那份原版 hookdll.dll（备份在 hookdll.original.dll）
#
# 加 -Publish 会一并处理发布目录（bin\Release\net10.0\...\publish\Scripts\civ6 和 bin\Release\net10.0\Scripts\civ6）。

param(
    [switch]$Diag,
    [switch]$Restore,
    [switch]$Publish,
    [string]$Target
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$forwardDll = Join-Path $root 'build\hookdll.dll'
$diagDll = Join-Path (Split-Path $root -Parent) 'civ6hook-diag\build\hookdll.dll'

function Get-TargetDirectories {
    param([string]$Explicit, [switch]$IncludePublish)

    if ($Explicit) {
        if (Test-Path $Explicit) { return @((Get-Item $Explicit).FullName) }
        throw "指定的目录不存在：$Explicit"
    }

    $candidates = @(
        (Join-Path $root '..\..\kiriyamalauncher.Presentation\Scripts\civ6')
    )

    if ($IncludePublish) {
        $candidates += @(
            (Join-Path $root '..\..\kiriyamalauncher.Presentation\bin\Release\net10.0\Scripts\civ6'),
            (Join-Path $root '..\..\kiriyamalauncher.Presentation\bin\Release\net10.0\win-x64\publish\Scripts\civ6')
        )
    }

    $found = @()

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $found += (Get-Item $candidate).FullName
        }
    }

    if ($found.Count -eq 0) {
        throw "找不到 Scripts\civ6 目录，用 -Target 手动指定。"
    }

    return $found
}

$source = if ($Diag) { $diagDll } else { $forwardDll }

if (-not $Restore -and -not (Test-Path $source)) {
    Write-Host "找不到要安装的 DLL：$source" -ForegroundColor Red
    Write-Host "先运行 .\build-hook.ps1（正式版）或 ..\civ6hook-diag\build-diag.ps1（诊断版）。" -ForegroundColor Yellow
    return
}

$game = Get-Process -Name 'CivilizationVI*' -ErrorAction SilentlyContinue

if ($game) {
    Write-Host "文明 6 还在运行（PID $($game.Id -join ', ')），请先关掉游戏。" -ForegroundColor Yellow
    return
}

foreach ($directory in (Get-TargetDirectories -Explicit $Target -IncludePublish:$Publish)) {
    Write-Host "目标目录：$directory" -ForegroundColor Cyan

    $hookDll = Join-Path $directory 'hookdll.dll'
    $hookDll64 = Join-Path $directory 'hookdll64.dll'
    $original = Join-Path $directory 'hookdll.original.dll'

    if ($Restore) {
        if (-not (Test-Path $original)) {
            Write-Host "  没有备份 hookdll.original.dll，跳过。" -ForegroundColor Yellow
            continue
        }

        Copy-Item $original $hookDll -Force
        Write-Host "  已还原原版 hookdll.dll" -ForegroundColor Green

        if (Test-Path $hookDll64) {
            Copy-Item $original $hookDll64 -Force
            Write-Host "  已还原原版 hookdll64.dll" -ForegroundColor Green
        }

        continue
    }

    if (-not (Test-Path $original)) {
        Copy-Item $hookDll $original -Force
        Write-Host "  已备份当前 hookdll.dll → hookdll.original.dll" -ForegroundColor DarkGray
    }

    Copy-Item $source $hookDll -Force
    Write-Host ("  已安装 {0}" -f (Split-Path $source -Leaf)) -ForegroundColor Green

    if (Test-Path $hookDll64) {
        Copy-Item $source $hookDll64 -Force
        Write-Host ("  已安装 {0}（hookdll64.dll）" -f (Split-Path $source -Leaf)) -ForegroundColor Green
    }
}

Write-Host ""

if ($Restore) {
    Write-Host "已还原原版。想再装回转发版就再运行一次本脚本。" -ForegroundColor Cyan
}
elseif ($Diag) {
    Write-Host "已安装诊断版（只记录、不转发）。" -ForegroundColor Cyan
}
else {
    Write-Host "已安装正式版转发 Hook。别忘了启动器的「游戏隧道」要开着（房间里的网络工具，或进游戏前先连上虚拟局域网）。" -ForegroundColor Cyan
}
