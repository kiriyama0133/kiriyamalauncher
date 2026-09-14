using System;
using System.Runtime.InteropServices;

namespace kiriyamalauncher.Data;

/// <summary>
/// libzt.dll 的 BSD 风格接口声明。
///
/// 这里的方法签名是照着托管包装 ZeroTier.Sockets 自己的 DllImport 声明抄的
/// （指针一律 IntPtr、长度用 UInt16/UInt32），保证和原生库的约定一致。
/// </summary>
internal static class LibztNative
{
    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_socket")]
    internal static extern int Socket(int family, int type, int protocol);

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_bind")]
    internal static extern int Bind(int fd, IntPtr address, ushort addressLength);

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_setsockopt")]
    internal static extern int SetSockOpt(int fd, int level, int optionName, IntPtr optionValue, ushort optionLength);

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_sendto")]
    internal static extern int SendTo(int fd, IntPtr buffer, uint length, int flags, IntPtr address, ushort addressLength);

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_recvfrom")]
    internal static extern int RecvFrom(int fd, IntPtr buffer, uint length, int flags, IntPtr address, IntPtr addressLength);

    [DllImport("libzt", EntryPoint = "CSharp_zts_get_data_available")]
    internal static extern int GetDataAvailable(int fd);

    /// <summary>
    /// 用 libzt 自己的助手把 "ip:port" 转成它要的 sockaddr 结构。
    ///
    /// 注意：libzt 的 sockaddr 布局和标准 sockaddr_in 不完全一样
    /// （实测手工拼 16 字节标准结构会让 bind 返回 EINVAL 22），必须用它这个助手来造。
    /// </summary>
    [DllImport("libzt", EntryPoint = "CSharp_zts_util_ipstr_to_saddr")]
    internal static extern int IpStrToSockAddr(string ip, int port, IntPtr sockAddr, IntPtr address);

    [DllImport("libzt", EntryPoint = "CSharp_zts_errno_get")]
    internal static extern int GetErrNo();

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_close")]
    internal static extern int Close(int fd);
}
