using System;
using System.Net;
using System.Net.Sockets;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IUdpTransport"/> 的两个实现共享的最小能力面：
/// 打开 / 发送 / 轮询接收，语义对齐 <see cref="LibztUdpSocket"/>。
/// </summary>
internal interface IUdpTransport : IDisposable
{
    /// <summary>socket 是否已经创建并绑定成功。</summary>
    bool IsOpen { get; }

    /// <summary>最近一次失败的原因（给日志用）。</summary>
    string LastError { get; }

    /// <summary>当前可读的字节数（非阻塞轮询用）。</summary>
    int BytesAvailable { get; }

    /// <summary>创建并绑定到 0.0.0.0:<paramref name="port"/>；失败返回 false（不抛异常）。</summary>
    bool Open(int port);

    /// <summary>发送一个 UDP 报文，返回发送的字节数（失败返回 -1）。</summary>
    int Send(string address, int port, byte[] payload);

    /// <summary>接收一个 UDP 报文，并解析出对方地址；无数据时返回 0。</summary>
    int Receive(byte[] buffer, out string senderAddress, out int senderPort);
}

/// <summary>
/// 基于 .NET <see cref="UdpClient"/> 的 UDP 传输（走系统真实网卡）。
///
/// 「嵌入的 ZeroTier 客户端」引擎下有真虚拟网卡，系统协议栈直接可达虚拟局域网，
/// 广播也由 OS 处理 —— 不需要（也不能）走 libzt 的网络栈。
/// </summary>
internal sealed class ManagedUdpSocket : IUdpTransport
{
    private readonly object _sync = new();

    private UdpClient? _client;

    /// <inheritdoc />
    public bool IsOpen => _client is not null;

    /// <inheritdoc />
    public string LastError { get; private set; } = string.Empty;

    /// <inheritdoc />
    public int BytesAvailable => _client?.Available ?? 0;

    /// <inheritdoc />
    public bool Open(int port)
    {
        lock (_sync)
        {
            if (_client is not null)
            {
                return true;
            }

            try
            {
                UdpClient client = new(new IPEndPoint(IPAddress.Any, port))
                {
                    EnableBroadcast = true
                };

                _client = client;
                LastError = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <inheritdoc />
    public int Send(string address, int port, byte[] payload)
    {
        UdpClient? client = _client;

        if (client is null)
        {
            return -1;
        }

        try
        {
            return client.Send(payload, payload.Length, address, port);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return -1;
        }
    }

    /// <inheritdoc />
    public int Receive(byte[] buffer, out string senderAddress, out int senderPort)
    {
        senderAddress = string.Empty;
        senderPort = 0;

        UdpClient? client = _client;

        if (client is null || client.Available <= 0)
        {
            return 0;
        }

        try
        {
            IPEndPoint? remote = null;
            byte[] data = client.Receive(ref remote);

            int length = Math.Min(data.Length, buffer.Length);
            Array.Copy(data, buffer, length);

            senderAddress = remote?.Address.ToString() ?? string.Empty;
            senderPort = remote?.Port ?? 0;

            return length;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return 0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}

/// <summary>
/// <see cref="LibztUdpSocket"/> 的接口化包装（libzt 网络栈内的 UDP 广播，
/// 供 Sockets / libzt 引擎在无虚拟网卡时使用）。
/// </summary>
internal sealed class LibztUdpTransport : IUdpTransport
{
    private readonly LibztUdpSocket _socket = new();

    public bool IsOpen => _socket.IsOpen;

    public string LastError => _socket.LastError;

    public int BytesAvailable => _socket.BytesAvailable;

    public bool Open(int port) => _socket.Open(port);

    public int Send(string address, int port, byte[] payload) => _socket.Send(address, port, payload);

    public int Receive(byte[] buffer, out string senderAddress, out int senderPort)
        => _socket.Receive(buffer, out senderAddress, out senderPort);

    public void Dispose() => _socket.Dispose();
}
