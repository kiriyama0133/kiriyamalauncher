using System;
using System.Net;
using System.Runtime.InteropServices;

namespace kiriyamalauncher.Data;

/// <summary>
/// libzt 的 UDP socket 封装（直接 P/Invoke libzt.dll）。
///
/// 托管包装 ZeroTier.Sockets 只暴露了「连接式」Send / Receive，没有广播能力，
/// 所以这里用与托管包装同一套 CSharp_zts_* 约定直接调用 BSD 风格接口：
/// 创建 socket → bind → sendto / recvfrom，用来在虚拟局域网里做 UDP 广播发现。
/// </summary>
internal sealed class LibztUdpSocket : IDisposable
{
    private const int AF_INET = 2;
    private const int SOCK_DGRAM = 2;
    private const int IPPROTO_UDP = 17;

    /// <summary>libzt 是 mingw 构建的，socket 选项用 Windows(Winsock) 的取值。</summary>
    private const int SOL_SOCKET = 0xFFFF;
    private const int SO_BROADCAST = 0x0020;

    /// <summary>sockaddr_in 的长度。</summary>
    private const int SOCKADDR_LENGTH = 16;

    private readonly object _sync = new();

    private int _fd = -1;

    /// <summary>socket 是否已经创建并绑定成功。</summary>
    public bool IsOpen => _fd >= 0;

    /// <summary>创建并绑定到 0.0.0.0:<paramref name="port"/>；失败返回 false（不抛异常）。</summary>
    public bool Open(int port)
    {
        lock (_sync)
        {
            if (_fd >= 0)
            {
                return true;
            }

            int fd = LibztNative.Socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);

            if (fd < 0)
            {
                LastError = $"socket() 返回 {fd}，errno={LibztNative.GetErrNo()}";
                return false;
            }

            EnableBroadcast(fd);

            int bindResult = Bind(fd, "0.0.0.0", port);

            if (bindResult < 0)
            {
                LastError = $"bind(0.0.0.0:{port}) 返回 {bindResult}，errno={LibztNative.GetErrNo()}";
                LibztNative.Close(fd);
                return false;
            }

            _fd = fd;
            LastError = string.Empty;
            return true;
        }
    }

    /// <summary>最近一次失败的原因（给日志用）。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>发送一个 UDP 报文。</summary>
    public int Send(string address, int port, byte[] payload)
    {
        int fd = _fd;

        if (fd < 0)
        {
            return -1;
        }

        byte[] socketAddress = BuildSocketAddress(address, port);

        IntPtr bufferPointer = Marshal.AllocHGlobal(payload.Length);
        IntPtr addressPointer = Marshal.AllocHGlobal(socketAddress.Length);

        try
        {
            Marshal.Copy(payload, 0, bufferPointer, payload.Length);
            Marshal.Copy(socketAddress, 0, addressPointer, socketAddress.Length);

            return LibztNative.SendTo(fd, bufferPointer, (uint)payload.Length, 0, addressPointer, (ushort)socketAddress.Length);
        }
        catch (Exception)
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(bufferPointer);
            Marshal.FreeHGlobal(addressPointer);
        }
    }

    /// <summary>当前可读的字节数（用来做非阻塞轮询，避免用到平台相关的 poll 常量）。</summary>
    public int BytesAvailable => _fd >= 0 ? LibztNative.GetDataAvailable(_fd) : 0;

    /// <summary>接收一个 UDP 报文，并解析出对方地址。</summary>
    public int Receive(byte[] buffer, out string senderAddress, out int senderPort)
    {
        senderAddress = string.Empty;
        senderPort = 0;

        int fd = _fd;

        if (fd < 0)
        {
            return -1;
        }

        IntPtr bufferPointer = Marshal.AllocHGlobal(buffer.Length);
        IntPtr addressPointer = Marshal.AllocHGlobal(SOCKADDR_LENGTH);
        IntPtr lengthPointer = Marshal.AllocHGlobal(sizeof(int));

        try
        {
            Marshal.WriteInt32(lengthPointer, SOCKADDR_LENGTH);

            int received = LibztNative.RecvFrom(fd, bufferPointer, (uint)buffer.Length, 0, addressPointer, lengthPointer);

            if (received <= 0)
            {
                return received;
            }

            Marshal.Copy(bufferPointer, buffer, 0, received);

            byte[] socketAddress = new byte[SOCKADDR_LENGTH];
            Marshal.Copy(addressPointer, socketAddress, 0, SOCKADDR_LENGTH);

            senderPort = (socketAddress[2] << 8) | socketAddress[3];
            senderAddress = new IPAddress(socketAddress[4..8]).ToString();

            return received;
        }
        catch (Exception)
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(bufferPointer);
            Marshal.FreeHGlobal(addressPointer);
            Marshal.FreeHGlobal(lengthPointer);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_fd < 0)
            {
                return;
            }

            LibztNative.Close(_fd);
            _fd = -1;
        }
    }

    private static void EnableBroadcast(int fd)
    {
        IntPtr valuePointer = Marshal.AllocHGlobal(sizeof(int));

        try
        {
            Marshal.WriteInt32(valuePointer, 1);

            // 失败也没关系：真正的广播能不能发出去由 libzt / 网络决定。
            LibztNative.SetSockOpt(fd, SOL_SOCKET, SO_BROADCAST, valuePointer, sizeof(int));
        }
        catch (Exception)
        {
            // 忽略。
        }
        finally
        {
            Marshal.FreeHGlobal(valuePointer);
        }
    }

    private static int Bind(int fd, string address, int port)
    {
        byte[] socketAddress = BuildSocketAddress(address, port);

        IntPtr addressPointer = Marshal.AllocHGlobal(socketAddress.Length);

        try
        {
            Marshal.Copy(socketAddress, 0, addressPointer, socketAddress.Length);

            return LibztNative.Bind(fd, addressPointer, (ushort)socketAddress.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(addressPointer);
        }
    }

    /// <summary>
    /// 拼 libzt 要的地址结构（16 字节：port 在大端序的第 2、3 字节，IPv4 在第 4–7 字节）。
    /// 必须用 libzt 自己的 <c>zts_util_ipstr_to_saddr</c>，手工拼标准 sockaddr_in 会让 bind 报 EINVAL。
    /// </summary>
    private static byte[] BuildSocketAddress(string address, int port)
    {
        byte[] socketAddress = new byte[SOCKADDR_LENGTH];

        // 交给 libzt 自己的助手填（手工拼标准 sockaddr_in 会让 bind 报 EINVAL 22）。
        IntPtr socketAddressPointer = Marshal.AllocHGlobal(SOCKADDR_LENGTH);
        IntPtr scratchPointer = Marshal.AllocHGlobal(sizeof(int));

        try
        {
            Marshal.Copy(new byte[SOCKADDR_LENGTH], 0, socketAddressPointer, SOCKADDR_LENGTH);
            Marshal.WriteInt32(scratchPointer, 0);

            if (LibztNative.IpStrToSockAddr(address, port, socketAddressPointer, scratchPointer) < 0)
            {
                // 助手失败时退回手工布局（至少能试一把）。
                socketAddress[2] = (byte)(port >> 8);
                socketAddress[3] = (byte)(port & 0xFF);

                byte[] fallback = IPAddress.Parse(address).GetAddressBytes();
                Array.Copy(fallback, 0, socketAddress, 4, Math.Min(fallback.Length, 4));

                return socketAddress;
            }

            Marshal.Copy(socketAddressPointer, socketAddress, 0, SOCKADDR_LENGTH);
        }
        finally
        {
            Marshal.FreeHGlobal(socketAddressPointer);
            Marshal.FreeHGlobal(scratchPointer);
        }

        return socketAddress;
    }
}
