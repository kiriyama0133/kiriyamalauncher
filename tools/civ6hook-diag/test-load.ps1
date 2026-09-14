# 自检：把诊断 Hook 加载进「当前 PowerShell 进程」，然后自己做一次
# UDP 广播发送 + 收包，验证 Hook 能不能正常挂钩、记账、写日志。
#
# 注意：这会 Hook 掉本 PowerShell 进程自己的 Winsock 调用，所以脚本跑完就退出，
#       不要拿它当常驻的 PowerShell 用。
#
# 用法：
#   .\test-load.ps1                       # 用 build\hookdll.dll
#   .\test-load.ps1 -DllPath .\kit\hookdll.dll

param(
    [string]$DllPath = (Join-Path $PSScriptRoot 'build\hookdll.dll'),
    [switch]$NoEat,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if ($NoEat) {
    $env:KIRIYAMA_DIAG_NOEAT = '1'
}

if ($Quiet) {
    $env:KIRIYAMA_DIAG_QUIET = '1'
}

if (-not (Test-Path $DllPath)) {
    Write-Host "找不到 $DllPath，请先运行 .\build-diag.ps1。" -ForegroundColor Red
    return
}

$full = (Get-Item $DllPath).FullName
$dllDirectory = Split-Path -Parent $full
Write-Host "加载：$full"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NativeLoader
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string fileName);
}
'@

$handle = [NativeLoader]::LoadLibraryW($full)

if ($handle -eq [IntPtr]::Zero) {
    Write-Host "加载失败，Win32 错误码：$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())" -ForegroundColor Red
    return
}

Write-Host "DLL 已加载（句柄 $handle），等 2 秒让挂钩线程跑起来……" -ForegroundColor Cyan
Start-Sleep -Seconds 2

# 自己造一次「文明 6 式」的局域网广播：绑一个随机端口，往 255.255.255.255:62999 发 4 字节，
# 再看有没有收到数据（收不到是正常的，这里只看 Hook 有没有记下来）。
$client = New-Object System.Net.Sockets.UdpClient
$client.EnableBroadcast = $true
$client.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)))
$client.Client.ReceiveTimeout = 300

$probe = [byte[]](0x4B, 0x49, 0x52, 0x49)   # "KIRI"
[void]$client.Send($probe, $probe.Length, (New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Broadcast, 62999)))

try {
    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    [void]$client.Receive([ref]$remote)
}
catch [System.Net.Sockets.SocketException] {
    Write-Host "（本地没有回包，符合预期）" -ForegroundColor DarkGray
}

Write-Host "等待日志落盘（期间保持 socket 打开，好让「本进程端口快照」记到它）……" -ForegroundColor Cyan
Start-Sleep -Seconds 6

$client.Close()
Start-Sleep -Seconds 1

$log = Get-ChildItem $dllDirectory -Filter 'civ6-hook-diag-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $log) {
    Write-Host "没有生成日志 —— Hook 没有正常工作。" -ForegroundColor Red
    return
}

Write-Host ""
Write-Host "日志：$($log.FullName)" -ForegroundColor Green

$lines = Get-Content -LiteralPath $log.FullName
$lines | Select-Object -Last 30

$summary = Join-Path $dllDirectory 'civ6-hook-diag-summary.txt'

if (Test-Path $summary) {
    Write-Host ""
    Write-Host "摘要：$summary" -ForegroundColor Green
    Get-Content -LiteralPath $summary
}
