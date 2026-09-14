# 单独验证 libzt 的 UDP socket 调用（socket / bind / setsockopt）到底哪一步失败。
#
# 说明：PowerShell 的 DllImport 解析会被它自己的 AssemblyLoadContext 挡住，
# 所以这里用 NativeLibrary.Load + GetDelegateForFunctionPointer 直接调用。
# 会在 %TEMP% 下建一个临时存储目录起一个全新节点（不加入任何网络，不影响正在运行的应用）。

param(
    [string]$LibztDirectory = 'F:\KiriyamaLauncher\kiriyamalauncher\kiriyamalauncher.Presentation\bin\Release\net10.0',
    [int]$Port = 43334
)

$ErrorActionPreference = 'Stop'

$libztPath = Join-Path $LibztDirectory 'libzt.dll'

if (-not (Test-Path $libztPath)) {
    Write-Host "找不到 $libztPath，请用 -LibztDirectory 指定路径。" -ForegroundColor Red
    return
}

$handle = [System.Runtime.InteropServices.NativeLibrary]::Load($libztPath)
Write-Host "已加载 $libztPath"

# GetDelegateForFunctionPointer 不接受泛型委托，所以先用 C# 定义一组具名委托类型。
Add-Type -TypeDefinition @'
using System;

public static class KiriLibztDelegates
{
    public delegate int IntFn();
    public delegate int StringFn(string value);
    public delegate int IpStrToSockAddrFn(string ip, int port, IntPtr sockAddr, IntPtr address);
    public delegate int TwoUShortFn(ushort first, ushort second);
    public delegate int SocketFn(int family, int type, int protocol);
    public delegate int BindFn(int fd, IntPtr address, ushort addressLength);
    public delegate int SetSockOptFn(int fd, int level, int optionName, IntPtr optionValue, ushort optionLength);
    public delegate int IntArgFn(int value);
}
'@

function Get-Export([string]$name, [Type]$delegateType) {
    $pointer = [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, $name)

    if ($pointer -eq [IntPtr]::Zero) {
        throw "找不到导出函数 $name"
    }

    return [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($pointer, $delegateType)
}

$initFromStorage = Get-Export 'CSharp_zts_init_from_storage' ([KiriLibztDelegates+StringFn])
$setPortRange = Get-Export 'CSharp_zts_init_set_random_port_range' ([KiriLibztDelegates+TwoUShortFn])
$nodeStart = Get-Export 'CSharp_zts_node_start' ([KiriLibztDelegates+IntFn])
$nodeIsOnline = Get-Export 'CSharp_zts_node_is_online' ([KiriLibztDelegates+IntFn])
$createSocket = Get-Export 'CSharp_zts_bsd_socket' ([KiriLibztDelegates+SocketFn])
$bindSocket = Get-Export 'CSharp_zts_bsd_bind' ([KiriLibztDelegates+BindFn])
$setSockOpt = Get-Export 'CSharp_zts_bsd_setsockopt' ([KiriLibztDelegates+SetSockOptFn])
$getErrNo = Get-Export 'CSharp_zts_errno_get' ([KiriLibztDelegates+IntFn])
$closeSocket = Get-Export 'CSharp_zts_bsd_close' ([KiriLibztDelegates+IntArgFn])
$ipStrToSockAddr = Get-Export 'CSharp_zts_util_ipstr_to_saddr' ([KiriLibztDelegates+IpStrToSockAddrFn])

$storage = Join-Path $env:TEMP ('libzt-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $storage | Out-Null

Write-Host "临时存储：$storage"
Write-Host ("init_from_storage = {0}" -f $initFromStorage.Invoke($storage))
Write-Host ("set_port_range    = {0}" -f $setPortRange.Invoke(41000, 49000))
Write-Host ("node_start        = {0}" -f $nodeStart.Invoke())

Start-Sleep -Seconds 3
Write-Host ("node_is_online    = {0}" -f $nodeIsOnline.Invoke())
Write-Host ""

$fd = $createSocket.Invoke(2, 2, 17)   # AF_INET=2, SOCK_DGRAM=2, IPPROTO_UDP=17
Write-Host ("socket(AF_INET, DGRAM, UDP) = {0}   errno={1}" -f $fd, $getErrNo.Invoke())

if ($fd -lt 0) {
    Write-Host "socket 就创建失败了。" -ForegroundColor Yellow
    return
}

function New-SockAddrBytes([string]$ip, [int]$port) {
    $bytes = New-Object byte[] 16
    $bytes[0] = 2
    $bytes[1] = 0
    $bytes[2] = [byte]($port -shr 8)
    $bytes[3] = [byte]($port -band 0xFF)
    $octets = ([System.Net.IPAddress]::Parse($ip)).GetAddressBytes()
    [Array]::Copy($octets, 0, $bytes, 4, 4)
    return $bytes
}

foreach ($target in @(@('0.0.0.0', $Port), @('127.0.0.1', $Port), @('0.0.0.0', 0))) {
    $pointer = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(16)
    $addressPointer = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(4)

    try {
        # 先用 libzt 自己的助手把 ip:port 转成它要的 sockaddr
        $convertResult = $ipStrToSockAddr.Invoke($target[0], $target[1], $pointer, $addressPointer)
        $raw = New-Object byte[] 16
        [System.Runtime.InteropServices.Marshal]::Copy($pointer, $raw, 0, 16)
        Write-Host ("ipstr_to_saddr({0}:{1}) = {2}   errno={3}   bytes={4}" -f $target[0], $target[1], $convertResult, $getErrNo.Invoke(), (($raw | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))

        $result = $bindSocket.Invoke($fd, $pointer, 16)
        Write-Host ("bind({0}:{1}) = {2}   errno={3}" -f $target[0], $target[1], $result, $getErrNo.Invoke())

        if ($result -eq 0) {
            break
        }
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($pointer)
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($addressPointer)
    }
}

$one = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(4)

try {
    [System.Runtime.InteropServices.Marshal]::WriteInt32($one, 1)
    Write-Host ("setsockopt(SO_BROADCAST, SOL_SOCKET=0xFFFF) = {0}   errno={1}" -f $setSockOpt.Invoke($fd, 0xFFFF, 0x0020, $one, 4), $getErrNo.Invoke())
    Write-Host ("setsockopt(SO_BROADCAST, SOL_SOCKET=1)      = {0}   errno={1}" -f $setSockOpt.Invoke($fd, 1, 6, $one, 4), $getErrNo.Invoke())
}
finally {
    [System.Runtime.InteropServices.Marshal]::FreeHGlobal($one)
}

Write-Host ("close = {0}" -f $closeSocket.Invoke($fd))
