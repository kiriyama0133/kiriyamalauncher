# 隧道自测：直接连到启动器的命名管道（模拟游戏侧 Hook），验证「管道 ⇄ libzt 单播」这条链路。
#
# 前提：两岸的应用都点过「开启隧道（选中设备）」。
#   A 机： .\tunnel-pipe-test.ps1 -Mode send  -SourcePort 51839 -DestinationPort 62999 -Text "hello"
#   B 机： .\tunnel-pipe-test.ps1 -Mode recv
# 也可以 A 机 -Mode echo，B 机 -Mode send，验证双向（发送方会打印收到的回包）。

param(
    [ValidateSet('send', 'recv', 'echo')]
    [string]$Mode = 'recv',
    [int]$SourcePort = 51839,
    [int]$DestinationPort = 62999,
    [string]$Text = 'hello-from-kiriyama',
    [int]$Count = 3,
    [int]$Seconds = 30,
    [string]$PipeName = 'kiriyama-lan-tunnel'
)

$ErrorActionPreference = 'Stop'

function New-Frame([byte]$type, [int]$srcPort, [int]$dstPort, [byte[]]$payload) {
    $frame = New-Object byte[] (8 + $payload.Length)
    $frame[0] = $type
    $frame[1] = 0
    [BitConverter]::GetBytes([uint16]$srcPort).CopyTo($frame, 2)
    [BitConverter]::GetBytes([uint16]$dstPort).CopyTo($frame, 4)
    [BitConverter]::GetBytes([uint16]$payload.Length).CopyTo($frame, 6)
    [Array]::Copy($payload, 0, $frame, 8, $payload.Length)
    return $frame
}

function Read-Frame([System.IO.Stream]$stream) {
    $header = New-Object byte[] 8
    $read = 0

    while ($read -lt 8) {
        $n = $stream.Read($header, $read, 8 - $read)
        if ($n -le 0) { return $null }
        $read += $n
    }

    $type = $header[0]
    $srcPort = [BitConverter]::ToUInt16($header, 2)
    $dstPort = [BitConverter]::ToUInt16($header, 4)
    $length = [BitConverter]::ToUInt16($header, 6)
    $payload = New-Object byte[] $length
    $read = 0

    while ($read -lt $length) {
        $n = $stream.Read($payload, $read, $length - $read)
        if ($n -le 0) { return $null }
        $read += $n
    }

    return [pscustomobject]@{ Type = $type; SourcePort = $srcPort; DestinationPort = $dstPort; Payload = $payload }
}

Write-Host "连接管道 \\.\pipe\$PipeName ……"
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(5000)
Write-Host "已连接（模式：$Mode）" -ForegroundColor Green

try {
    if ($Mode -eq 'send') {
        $payload = [System.Text.Encoding]::UTF8.GetBytes($Text)

        for ($index = 1; $index -le $Count; $index++) {
            $frame = New-Frame 1 $SourcePort $DestinationPort $payload
            $pipe.Write($frame, 0, $frame.Length)
            $pipe.Flush()
            Write-Host "已发送出站帧：$SourcePort → 对端:$DestinationPort，$($payload.Length) 字节"
            Start-Sleep -Milliseconds 500
        }

        Write-Host "等待可能的回包（5 秒）……"
        $deadline = (Get-Date).AddSeconds(5)

        while ((Get-Date) -lt $deadline) {
            $frame = Read-Frame $pipe
            if ($null -eq $frame) { break }

            $text = [System.Text.Encoding]::UTF8.GetString($frame.Payload)
            Write-Host ("收到入站帧：对端:{0} → 本地:{1}，{2} 字节，内容：{3}" -f $frame.SourcePort, $frame.DestinationPort, $frame.Payload.Length, $text) -ForegroundColor Cyan
        }
    }
    elseif ($Mode -eq 'recv') {
        Write-Host "监听入站帧 $Seconds 秒（Ctrl+C 结束）……"
        $deadline = (Get-Date).AddSeconds($Seconds)

        while ((Get-Date) -lt $deadline) {
            $frame = Read-Frame $pipe
            if ($null -eq $frame) { Write-Host "管道已关闭。"; break }

            $text = [System.Text.Encoding]::UTF8.GetString($frame.Payload)
            Write-Host ("收到帧：type={0}，对端:{1} → 本地:{2}，{3} 字节，内容：{4}" -f $frame.Type, $frame.SourcePort, $frame.DestinationPort, $frame.Payload.Length, $text) -ForegroundColor Cyan
        }
    }
    else {
        Write-Host "回声模式：收到出站帧就原样回一个入站帧 $Seconds 秒……"
        $deadline = (Get-Date).AddSeconds($Seconds)

        while ((Get-Date) -lt $deadline) {
            $frame = Read-Frame $pipe
            if ($null -eq $frame) { Write-Host "管道已关闭。"; break }

            $text = [System.Text.Encoding]::UTF8.GetString($frame.Payload)
            Write-Host ("收到出站帧：$($frame.SourcePort) → $($frame.DestinationPort)，内容：$text") -ForegroundColor Cyan

            # 原样回一个入站帧：src/dst 互换
            $reply = New-Frame 2 $frame.DestinationPort $frame.SourcePort $frame.Payload
            $pipe.Write($reply, 0, $reply.Length)
            $pipe.Flush()
            Write-Host ("已回包：{0} → {1}" -f $frame.DestinationPort, $frame.SourcePort)
        }
    }
}
finally {
    $pipe.Dispose()
    Write-Host "已断开。"
}
