using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏 Hook DLL 与启动器之间的管道协议。
///
/// 帧格式（小端）：
///     type(1) | flags(1) | srcPort(2) | dstPort(2) | length(2) | ipv4(4) | payload(length)
///
/// type = 1（出站，游戏 → 隧道）：
///     srcPort = 游戏那个 socket 的本地端口（getsockname 得到）
///     dstPort = 游戏要发往的目标端口
///     ipv4    = 游戏要发往的目标地址（网络字节序）
///     flags bit0 = 1 → 这是广播包（255.255.255.255 或 x.x.x.255），要发给**所有**对端；
///                  否则按 ipv4 只发给对应的那台对端。
///   隧道会：把本机虚拟网 socket 绑到 srcPort，再按上面的规则转发。
///   为什么端口要一起带：游戏用 51839 发到 62999，对方的回复必须回到 51839。
///
/// type = 2（入站，隧道 → 游戏）：
///     srcPort = 对端的源端口（游戏把它当作「对端地址的端口」）
///     dstPort = 要投递到的本地端口（游戏侧 socket 的本地端口）
///     ipv4    = 对端的源地址（网络字节序）
///   每个人的包都带自己的地址，这样多人时房主才知道回包该发给谁。
///
/// type = 3（控制，隧道 → 游戏）：
///     ipv4 = 本机虚拟 IP。Hook 用它算出自己的虚拟网段（/24），
///            凡是发往这个网段的单播包都交给隧道转发。
///
/// type = 9（握手，游戏 → 隧道）：Hook 连上后发的空帧，表示"我准备好了"。
///
/// type = 4（监听，游戏 → 隧道）：dstPort = 游戏刚刚 bind 的本地端口。
///   隧道要在这个端口上也能收（否则对端发到「房主的 62900」这种监听端口的包会被丢掉）。
/// </summary>
internal static class LanTunnelProtocol
{
    /// <summary>出站：游戏 → 隧道。</summary>
    internal const byte FRAME_OUTBOUND = 1;

    /// <summary>入站：隧道 → 游戏。</summary>
    internal const byte FRAME_INBOUND = 2;

    /// <summary>控制：隧道 → 游戏（告知本机虚拟 IP）。</summary>
    internal const byte FRAME_CONTROL = 3;

    /// <summary>握手：游戏 → 隧道。</summary>
    internal const byte FRAME_HELLO = 9;

    /// <summary>监听：游戏 → 隧道（请隧道在虚拟网上也打开这个端口）。</summary>
    internal const byte FRAME_LISTEN = 4;

    /// <summary>flags bit0：广播包，要发给所有对端。</summary>
    internal const byte FLAG_BROADCAST = 1;

    /// <summary>帧头长度。</summary>
    internal const int HEADER_LENGTH = 12;

    /// <summary>单个报文最大长度（UDP 上限）。</summary>
    internal const int MAX_PAYLOAD_LENGTH = 65507;

    internal static byte[] BuildFrame(byte type, int sourcePort, int destinationPort, ReadOnlySpan<byte> payload,
        IPAddress? address = null, byte flags = 0)
    {
        byte[] frame = new byte[HEADER_LENGTH + payload.Length];

        frame[0] = type;
        frame[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)payload.Length);
        (address ?? IPAddress.Any).GetAddressBytes().CopyTo(frame.AsSpan(8, 4));
        payload.CopyTo(frame.AsSpan(HEADER_LENGTH));

        return frame;
    }

    /// <summary>读取一帧；流结束时返回 null。</summary>
    internal static async Task<(byte Type, byte Flags, int SourcePort, int DestinationPort, IPAddress Address, byte[] Payload)?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HEADER_LENGTH];

        if (!await ReadExactAsync(stream, header, cancellationToken))
        {
            return null;
        }

        byte type = header[0];
        byte flags = header[1];
        int sourcePort = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int destinationPort = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        IPAddress address = new(header.AsSpan(8, 4));

        if (length > MAX_PAYLOAD_LENGTH)
        {
            throw new InvalidDataException($"帧长度异常：{length}");
        }

        byte[] payload = new byte[length];

        if (length > 0 && !await ReadExactAsync(stream, payload, cancellationToken))
        {
            return null;
        }

        return (type, flags, sourcePort, destinationPort, address, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);

            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
