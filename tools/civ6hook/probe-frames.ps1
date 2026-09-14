# 对比两个 hook 版本「到底把哪些包转发出去了」。
#
# 做法：本脚本扮演启动器（命名管道服务端），子进程加载被对比的 hookdll，
# 然后依次往一批有代表性的「目标地址:端口」发包；脚本把 hook 转发出来的每一帧
# （type=1 出站）记录下来，写入 -ResultFile。
#
# 用法：  .\probe-frames.ps1 -DllPath build\hookdll.dll            -ResultFile $env:TEMP\new.txt
#         .\probe-frames.ps1 -DllPath build\hookdll-original.dll   -ResultFile $env:TEMP\old.txt

param(
    [Parameter(Mandatory = $true)][string]$DllPath,
    [Parameter(Mandatory = $true)][string]$ResultFile
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DllPath)) {
    throw "找不到 DLL：$DllPath"
}

$dll = (Get-Item $DllPath).FullName
$pipeName = "kiriyama-probe-{0}" -f (Get-Date -Format 'HHmmssfff')

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

function Read-Exact {
    param([System.IO.Stream]$Stream, [byte[]]$Buffer, [int]$TimeoutMs = 4000)

    $offset = 0

    while ($offset -lt $Buffer.Length) {
        $async = $Stream.BeginRead($Buffer, $offset, $Buffer.Length - $offset, $null, $null)

        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }

        try { $read = $Stream.EndRead($async) } catch { return $false }

        if ($read -le 0) { return $false }

        $offset += $read
    }

    return $true
}

function Write-Frame {
    param([System.IO.Stream]$Stream, [byte]$Type, [int]$SourcePort, [int]$DestinationPort, [byte[]]$Payload, [byte]$Flags = 0, [string]$Address = '0.0.0.0')

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

# 子进程：加载 hook → 从同一个 socket 依次发往这些目标
$childScript = @'
param([string]$DllPath)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class KiriProbeNative
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string fileName);
}
"@

[void][KiriProbeNative]::LoadLibraryW($DllPath)
Start-Sleep -Seconds 4

$client = New-Object System.Net.Sockets.UdpClient
$client.EnableBroadcast = $true
$client.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)))

$probe = [byte[]](0x8D, 0xE8, 0xFC, 0x70)

$targets = @(
    @{ ip = '255.255.255.255'; port = 62900 },   # 全局广播 + 局域网端口
    @{ ip = '255.255.255.255'; port = 50000 },   # 全局广播 + 普通端口
    @{ ip = '192.168.77.255';  port = 50000 },   # 子网广播 + 普通端口
    @{ ip = '192.168.77.255';  port = 62900 },   # 子网广播 + 局域网端口
    @{ ip = '127.0.0.1';       port = 62900 },   # 回环 + 局域网端口
    @{ ip = '127.0.0.1';       port = 50000 },   # 回环 + 普通端口
    @{ ip = '0.0.0.0';         port = 62900 },   # 未指定 + 局域网端口
    @{ ip = '0.0.0.0';         port = 50000 },   # 未指定 + 普通端口
    @{ ip = '10.99.99.99';     port = 62900 },   # 私有(非本机网段) + 局域网端口
    @{ ip = '10.99.99.99';     port = 50000 },   # 私有(非本机网段) + 普通端口
    @{ ip = '10.74.203.66';    port = 62056 },   # 虚拟网段内单播
    @{ ip = '8.8.8.8';         port = 50000 }    # 公网地址
)

foreach ($t in $targets) {
    try {
        [void]$client.Send($probe, $probe.Length, (New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse($t.ip), $t.port)))
    }
    catch {
        # 发不出去就算了（比如 0.0.0.0）
    }

    Start-Sleep -Milliseconds 80
}

Start-Sleep -Seconds 6
$client.Close()
'@

$childFile = Join-Path $env:TEMP ("civ6probe-{0}.ps1" -f (Get-Date -Format 'HHmmssfff'))
Set-Content -LiteralPath $childFile -Value $childScript -Encoding UTF8

$env:KIRIYAMA_LAN_PIPE = $pipeName
$env:KIRIYAMA_LAN_VERBOSE = '1'

$child = Start-Process -FilePath (Get-Process -Id $PID).Path `
    -ArgumentList @('-NoProfile', '-File', $childFile, $dll) `
    -PassThru -WindowStyle Hidden

$connect = $pipe.WaitForConnectionAsync()

if (-not $connect.Wait(20000)) {
    try { $child.Kill() } catch { }
    throw "hook 没有连上管道。"
}

# 先告诉 hook 本机虚拟 IP，这样"虚拟网段内的单播"这条规则才有意义。
Write-Frame -Stream $pipe -Type 3 -SourcePort 0 -DestinationPort 0 -Payload @() -Address '10.74.203.60'

$rows = New-Object System.Collections.Generic.List[string]
$header = New-Object byte[] 12
$deadline = (Get-Date).AddSeconds(16)

while ((Get-Date) -lt $deadline) {
    if (-not (Read-Exact -Stream $pipe -Buffer $header -TimeoutMs 3000)) { continue }

    $type = $header[0]
    $flags = $header[1]
    $src = [BitConverter]::ToUInt16($header, 2)
    $dst = [BitConverter]::ToUInt16($header, 4)
    $len = [BitConverter]::ToUInt16($header, 6)
    $addr = New-Object System.Net.IPAddress (,$header[8..11])

    $payload = New-Object byte[] $len
    if ($len -gt 0 -and -not (Read-Exact -Stream $pipe -Buffer $payload)) { break }

    if ($type -eq 1) {
        $rows.Add(("dst={0}:{1} flags={2} srcPort={3} len={4}" -f $addr, $dst, $flags, $src, $len))
    }
}

try { $pipe.Dispose() } catch { }
try { if (-not $child.HasExited) { $child.WaitForExit(5000) | Out-Null } } catch { }
try { if (-not $child.HasExited) { $child.Kill() } } catch { }

$sorted = $rows | Sort-Object -Unique
Set-Content -LiteralPath $ResultFile -Value $sorted -Encoding UTF8
Remove-Item -LiteralPath $childFile -ErrorAction SilentlyContinue

Write-Host ("转发帧种类 {0} 条 → {1}" -f $sorted.Count, $ResultFile)
