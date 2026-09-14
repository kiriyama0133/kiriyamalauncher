# 把「最新编译出来的诊断 Hook」+ 相关脚本装进 kit 目录（方便整个 kit 一起发出去）。
#
# 会自动挑最新编译出来的那份 DLL：
#   build\hookdll.dll      （源码目录里刚编译出来的）
#   kit\hookdll64.dll      （发布目录里带过来的新版本）
#   kit\hookdll.dll        （发布目录里的旧版本）
# 按文件修改时间取最新的一个，所以无论在源码目录还是发布目录里运行都合适。

$ErrorActionPreference = 'Continue'

$root = $PSScriptRoot
$kit = Join-Path $root 'kit'
$build = Join-Path $root 'build'

if (-not (Test-Path $kit)) {
    New-Item -ItemType Directory -Path $kit | Out-Null
}

$candidates = @(
    (Join-Path $build 'hookdll.dll'),
    (Join-Path $kit 'hookdll64.dll'),
    (Join-Path $kit 'hookdll.dll')
) | Where-Object { Test-Path $_ } | ForEach-Object { Get-Item $_ } | Sort-Object LastWriteTime -Descending

if (-not $candidates) {
    Write-Host "在 $root 和 $kit 里都没找到 hookdll.dll，请先运行 .\build-diag.ps1。" -ForegroundColor Red
    return
}

$source = $candidates[0]
Write-Host ("使用：{0}（{1:N1} KB，{2}）" -f $source.FullName, ($source.Length / 1KB), $source.LastWriteTime)

$game = Get-Process -Name 'CivilizationVI*' -ErrorAction SilentlyContinue

if ($game) {
    Write-Host "文明 6 还在运行（PID $($game.Id -join ', ')），DLL 被占用。请先关掉游戏再运行本脚本。" -ForegroundColor Yellow
    return
}

# 1) DLL：两个文件名都是同一份（injciv6.exe 会按目标位数挑名字）
foreach ($name in @('hookdll.dll', 'hookdll64.dll')) {
    $target = Join-Path $kit $name

    # 源文件就是目标文件时不用拷（否则 PowerShell 会报 "Cannot overwrite ... with itself"）。
    if ([System.IO.Path]::GetFullPath($target) -eq [System.IO.Path]::GetFullPath($source.FullName)) {
        Write-Host "$name 已经就是这份文件，跳过。" -ForegroundColor DarkGray
        continue
    }

    try {
        Copy-Item $source.FullName $target -Force -ErrorAction Stop
        Write-Host "已更新 $name" -ForegroundColor Green
    }
    catch {
        Write-Host "更新 $name 失败：$($_.Exception.Message)" -ForegroundColor Red
    }
}

# 2) 脚本与说明：一起放进 kit，方便整套拷走
foreach ($name in @('README.txt', 'build-diag.ps1', 'test-load.ps1', 'launcher-hook.ps1', 'run-diag.ps1', 'tunnel-pipe-test.ps1')) {
    $item = Join-Path $root $name

    if (-not (Test-Path $item)) {
        continue
    }

    try {
        Copy-Item $item (Join-Path $kit $name) -Force -ErrorAction Stop
        Write-Host "已更新 $name" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "更新 $name 失败：$($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "接下来（二选一）：" -ForegroundColor Cyan
Write-Host "  A. 让启动器自动注入：  .\launcher-hook.ps1" -ForegroundColor Cyan
Write-Host "  B. 手动注入：          先启动文明 6 → 进「多人游戏 → 局域网」→ 运行 .\run-diag.ps1" -ForegroundColor Cyan
