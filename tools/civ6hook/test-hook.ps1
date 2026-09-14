# 不上游戏，直接验证「正式版 Hook」的整条链路：
#   1) 本脚本扮演启动器，创建命名管道 kiriyama-lan-tunnel
#   2) 起一个子 PowerShell，把 hookdll.dll 加载进去（等于注入）
#   3) 子进程往 255.255.255.255:62999 发一个 4 字节广播
#      → 本脚本应当从管道收到「出站帧」，并且带上子进程那个 socket 的本地端口
#   4) 本脚本发回「控制帧」（对端虚拟 IP）+「入站帧」（假装是对端的房间应答）
#      → 子进程用 recvfrom 应当能收到这条数据，from 是对端虚拟 IP
#
# 用法：  .\test-hook.ps1

param(
    [string]$DllPath = (Join-Path $PSScriptRoot 'build\hookdll.dll'),
    [string]$PeerVirtualIp = '10.74.203.66',
    [int]$HookDelaySeconds = 3
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DllPath)) {
    Write-Host "找不到 $DllPath，请先运行 .\build-hook.ps1。" -ForegroundColor Red
    return
}

$dll = (Get-Item $DllPath).FullName
$resultFile = Join-Path $env:TEMP ("civ6hook-test-{0}.txt" -f (Get-Date -Format 'HHmmss'))

# 每次用一个新的管道名：避免和正在运行的启动器（或上一次测试）抢同一个管道。
$pipeName = "kiriyama-lan-tunnel-test-{0}" -f (Get-Date -Format 'HHmmss')

Write-Host "被注入的 DLL：$dll"
Write-Host "本脚本扮演启动器，监听管道 \\\\.\\pipe\\$pipeName ……"

# 和启动器保持一致：显式给宽松 ACL，否则别的进程可能连不上（拒绝访问）。
$security = New-Object System.IO.Pipes.PipeSecurity
$security.AddAccessRule((New-Object System.IO.Pipes.PipeAccessRule('Everyone', 'FullControl', 'Allow')))

$pipe = [System.IO.Pipes.NamedPipeServerStreamAcl]::Create(
    $pipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte,
    [System.IO.Pipes.PipeOptions]::None,
    4096,
    4096,
    $security)

function Write-Frame {
    param(
        [System.IO.Stream]$Stream,
        [byte]$Type,
        [int]$SourcePort,
        [int]$DestinationPort,
        [byte[]]$Payload,
        [byte]$Flags = 0,
        [string]$Address = '0.0.0.0'
    )

    $frame = New-Object byte[] (12 + $Payload.Length)
    $frame[0] = $Type
    $frame[1] = $Flags
    [BitConverter]::GetBytes([uint16]$SourcePort).CopyTo($frame, 2)
    [BitConverter]::GetBytes([uint16]$DestinationPort).CopyTo($frame, 4)
    [BitConverter]::GetBytes([uint16]$Payload.Length).CopyTo($frame, 6)
    [System.Net.IPAddress]::Parse($Address).GetAddressBytes().CopyTo($frame, 8)
    $Payload.CopyTo($frame, 12)

    $Stream.Write($frame, 0, $frame.Length)
    $Stream.Flush()
}

function Read-Exact {
    param([System.IO.Stream]$Stream, [byte[]]$Buffer, [int]$TimeoutMs = 15000)

    $offset = 0

    while ($offset -lt $Buffer.Length) {
        $async = $Stream.BeginRead($Buffer, $offset, $Buffer.Length - $offset, $null, $null)

        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            Write-Host "读管道超时。" -ForegroundColor DarkGray
            return $false
        }

        try {
            $read = $Stream.EndRead($async)
        }
        catch {
            Write-Host ("读管道失败（{0}）" -f $_.Exception.Message) -ForegroundColor DarkGray
            return $false
        }

        if ($read -le 0) { return $false }

        $offset += $read
    }

    return $true
}

# 子进程要干的活：加载 DLL → 发广播 → 等注入的数据
$childScript = @'
param([string]$DllPath, [string]$ResultFile)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class KiriNative
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string fileName);
}
"@

[void][KiriNative]::LoadLibraryW($DllPath)
Start-Sleep -Seconds 4

$client = New-Object System.Net.Sockets.UdpClient
$client.EnableBroadcast = $true
$client.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)))
$client.Client.ReceiveTimeout = 5000

$localPort = ([System.Net.IPEndPoint]$client.Client.LocalEndPoint).Port
$probe = [byte[]](0x8D, 0xE8, 0xFC, 0x70)
[void]$client.Send($probe, $probe.Length, (New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Broadcast, 62999)))

$lines = @("本地端口=$localPort")

try {
    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $data = $client.Receive([ref]$remote)
    $text = [System.Text.Encoding]::ASCII.GetString($data)
    $lines += "收到注入数据=$text 来自=$($remote.Address):$($remote.Port) 长度=$($data.Length)"
}
catch {
    $lines += "没有收到注入数据：$($_.Exception.Message)"
}

$client.Close()
Set-Content -LiteralPath $ResultFile -Value $lines -Encoding UTF8
'@

$childScriptFile = Join-Path $env:TEMP ("civ6hook-child-{0}.ps1" -f (Get-Date -Format 'HHmmss'))
Set-Content -LiteralPath $childScriptFile -Value $childScript -Encoding UTF8

# 打开详细日志，方便排查（Hook 在 DllMain 里读这些环境变量，子进程会继承）。
$env:KIRIYAMA_LAN_VERBOSE = '1'
$env:KIRIYAMA_LAN_PIPE = $pipeName

$child = Start-Process -FilePath (Get-Process -Id $PID).Path `
    -ArgumentList @('-NoProfile', '-File', $childScriptFile, $dll, $resultFile) `
    -PassThru -WindowStyle Hidden

Write-Host "等待子进程连上管道……" -ForegroundColor Cyan
$connectTask = $pipe.WaitForConnectionAsync()

if (-not $connectTask.Wait(15000)) {
    Write-Host "15 秒内没有 Hook 连上管道。" -ForegroundColor Red
    try { $child.Kill() } catch { }
    return
}

Write-Host "Hook 已连接，开始收帧……" -ForegroundColor Green

    $header = New-Object byte[] 12
$outboundPort = 0
$outboundLength = 0
$outboundOk = $false

for ($index = 0; $index -lt 20; $index++) {
    if (-not (Read-Exact -Stream $pipe -Buffer $header)) { break }

    $type = $header[0]
    $flags = $header[1]
    $sourcePort = [BitConverter]::ToUInt16($header, 2)
    $destinationPort = [BitConverter]::ToUInt16($header, 4)
    $length = [BitConverter]::ToUInt16($header, 6)
    $address = New-Object System.Net.IPAddress (,$header[8..11])

    $payload = New-Object byte[] $length

    if ($length -gt 0 -and -not (Read-Exact -Stream $pipe -Buffer $payload)) { break }

    Write-Host ("收到帧：type={0} flags={1} src={2} dst={3} addr={4} len={5} payload=[{6}]" -f `
        $type, $flags, $sourcePort, $destinationPort, $address, $length,
        (($payload | ForEach-Object { $_.ToString('X2') }) -join ' '))

    if ($type -eq 1) {
        $outboundOk = $true
        $outboundPort = $sourcePort
        $outboundLength = $length
        break
    }
}

if (-not $outboundOk) {
    Write-Host "没有收到出站帧 —— Hook 的转发没生效。" -ForegroundColor Red
}
else {
    Write-Host ("出站帧正常：游戏用本地端口 {0} 发了 {1} 字节广播。" -f $outboundPort, $outboundLength) -ForegroundColor Green

    # 控制帧：告诉 Hook 本机虚拟 IP（它用这个算出虚拟网段）
    Write-Frame -Stream $pipe -Type 3 -SourcePort 0 -DestinationPort 0 -Payload @() -Address '10.74.203.60'
    Write-Host "已发送控制帧（本机虚拟 IP 10.74.203.60）" -ForegroundColor Cyan

    # 入站帧：假装对端把房间信息回给了游戏那个端口
    $reply = [System.Text.Encoding]::ASCII.GetBytes('HELLO-FROM-PEER')
    Write-Frame -Stream $pipe -Type 2 -SourcePort 62900 -DestinationPort $outboundPort -Payload $reply -Address $PeerVirtualIp
    Write-Host "已发送入站帧（对端端口 62900 → 本地端口 $outboundPort）" -ForegroundColor Cyan
}

Start-Sleep -Seconds 2

try { if (-not $child.HasExited) { $child.WaitForExit(8000) | Out-Null } } catch { }

Write-Host ""

if (Test-Path $resultFile) {
    Write-Host "子进程结果：" -ForegroundColor Green
    Get-Content -LiteralPath $resultFile
}
else {
    Write-Host "子进程没有写出结果文件（可能崩了或者没跑完）。" -ForegroundColor Yellow
}

try { $pipe.Dispose() } catch { }

Remove-Item -LiteralPath $childScriptFile -ErrorAction SilentlyContinue
