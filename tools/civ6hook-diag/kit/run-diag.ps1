# 一键诊断：检测游戏进程 → 管理员权限注入诊断 DLL → 显示日志
# 用法：先启动文明 6 并进入「多人游戏 → 局域网」，然后运行本脚本（会弹 UAC）。
#
# 更省事的做法（推荐）：先用 .\launcher-hook.ps1 把诊断 Hook 装进启动器的自动注入路径，
# 这样用启动器启动游戏就会自动带上 Hook，不会漏掉最早的广播包。

$ErrorActionPreference = 'Continue'

# DLL 会一直占用日志文件，所以必须用共享读的方式打开。
function Show-LogTail {
    param([string]$Path, [int]$Count = 25)

    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            $lines = @()
            while (-not $reader.EndOfStream) { $lines += $reader.ReadLine() }
            $lines | Select-Object -Last $Count
        }
        finally {
            $reader.Close()
            $stream.Close()
        }
    }
    catch {
        Write-Host "读取日志失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

$game = Get-Process -Name 'CivilizationVI*' -ErrorAction SilentlyContinue

if (-not $game) {
    Write-Host "没有检测到 CivilizationVI* 进程。" -ForegroundColor Yellow
    Write-Host "请先启动文明 6，进入「多人游戏 → 局域网」界面，然后再运行本脚本。" -ForegroundColor Yellow
    return
}

foreach ($process in $game) {
    Write-Host "检测到游戏进程：$($process.ProcessName)（PID $($process.Id)）"
}

$injector = Join-Path $PSScriptRoot 'injciv6.exe'

if (-not (Test-Path $injector)) {
    Write-Host "找不到 $injector" -ForegroundColor Red
    return
}

Write-Host "正在以管理员身份运行注入工具（会弹出 UAC 确认框）……" -ForegroundColor Cyan
Start-Process -FilePath $injector -WorkingDirectory $PSScriptRoot -Verb RunAs

Write-Host "等待 8 秒后检查结果……" -ForegroundColor Cyan
Start-Sleep -Seconds 8

$marker = Join-Path $PSScriptRoot 'civ6hook-diag-loaded.txt'

if (Test-Path $marker) {
    Write-Host "注入成功：DLL 已被游戏加载。" -ForegroundColor Green
    Get-Content $marker
}
else {
    Write-Host "还没有看到加载标记 —— 注入可能没成功（权限 / 注入器找不到进程）。" -ForegroundColor Red
    Write-Host "可以先运行 .\test-load.ps1 确认 DLL 本身没问题。" -ForegroundColor Yellow
}

$log = Get-ChildItem $PSScriptRoot -Filter 'civ6-hook-diag-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($log) {
    Write-Host ""
    Write-Host "日志：$($log.FullName)" -ForegroundColor Green
    Show-LogTail -Path $log.FullName
}
else {
    Write-Host "（本目录暂时还没有日志文件）" -ForegroundColor Yellow
}
