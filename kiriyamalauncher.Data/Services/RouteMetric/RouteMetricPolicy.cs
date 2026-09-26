using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace kiriyamalauncher.Data;

/// <summary>
/// 网卡优先级（metric）的**跨平台判定策略**：把「谁是我的目标网卡」「谁在和我抢最高优先级」
/// 「它算不算虚拟网卡」「下调到多少」这些与操作系统无关的规则集中在一处；
/// 各平台实现只负责用本平台的命令（PowerShell/netsh、iproute2、ifconfig）去读/写 metric。
///
/// 为什么要处理「并列」：
///   系统 metric 的下限就是 1，我们把 ZeroTier 网卡设成 1 之后，如果机器上还有另一块网卡
///   同样是 1（很常见：用户手动把 Tailscale / Radmin / 另一块 ZeroTier 网卡也设成 1，
///   或者上一张网卡是 Windows 自动 metric 算出来的 1），两者就**并列**了。
///   并列时由系统内部规则（通常是接口索引/创建顺序）决定谁先，我们的网卡可能反而排在后面 ——
///   表现为「日志说成功，游戏流量仍然不走隧道」。所以把 metric 设好之后，必须再检查一次并列，
///   把同样处于最高优先级、但**不是我们**的虚拟网卡下调一档，让目标网卡严格胜出。
///
/// 只动**虚拟网卡**是刻意的：实体网卡承载用户正常的网络出口，改它的 metric 会牵连
/// 与联机无关的流量；真有实体网卡并列时，只记录日志并在提示里说明，不擅自改动。
/// </summary>
public static class RouteMetricPolicy
{
    /// <summary>最高优先级 metric：系统的下限（不能比 1 更小）。</summary>
    public const int TOP_METRIC = 1;

    /// <summary>
    /// 「虚拟网卡」名称关键字兜底表。仅在各平台拿不到权威标志时使用
    /// （Windows 用 <c>Get-NetAdapter.Virtual</c>、Linux 用 sysfs 的 device 软链，
    /// 只有 macOS 与部分降级路径靠这张表）。
    /// 含短前缀 <c>zt</c>（ZeroTier 在 Linux/macOS 上的网卡名就是 <c>ztXXXXXXXX</c>）
    /// 与 <c>wg</c>（WireGuard 的 <c>wg0</c>）—— 这类网卡名只有缩写，没有更长的品牌字样可匹配。
    /// </summary>
    private static readonly string[] VIRTUAL_INTERFACE_KEYWORDS =
    [
        "zerotier", "zt", "tailscale", "wireguard", "wg", "openvpn", "softether", "hamachi", "radmin", "wintun",
        "tun", "tap", "vpn", "utun", "feth", "ppp", "ipsec", "gif", "stf", "awdl", "llw", "anpi",
        "veth", "docker", "bridge", "br-", "virbr", "vmnet", "vmenet", "hyper-v", "vethernet",
        "vmware", "virtualbox", "virtual", "loopback"
    ];

    /// <summary>
    /// 目标 metric 冲突时的下调档位：比目标值大一档（1 → 2）。
    /// 只要严格大于目标值即可胜出，取最小增量是为了尽量少改动别人。
    /// </summary>
    public static int DemotedMetric(int targetMetric) => targetMetric + 1;

    /// <summary>
    /// 判断另一块网卡的 metric 是否与目标值**并列或更优**。
    /// 注意条件不是「等于」而是「≤」：理论上可能遇到 0（会压住我们的 1）。
    /// </summary>
    public static bool ConflictsWith(int? otherMetric, int targetMetric)
        => otherMetric is int value && value <= targetMetric;

    /// <summary>该网卡是否算虚拟网卡（系统没给出标志时按名称关键字兜底）。</summary>
    public static bool IsVirtualInterface(NetworkInterfaceInfo info)
        => info.IsVirtual ?? LooksLikeVirtualInterface(info.Name, info.DisplayName);

    /// <summary>按名称/描述关键字猜测是否虚拟网卡（兜底用）。</summary>
    public static bool LooksLikeVirtualInterface(string name, string displayName)
    {
        foreach (string keyword in VIRTUAL_INTERFACE_KEYWORDS)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从候选网卡里挑出目标 ZeroTier 网卡：优先精确命中网络 ID（网卡名通常带 ID 的低 16 位十六进制），
    /// 否则退化为第一块满足 <paramref name="isZeroTier"/> 的网卡。
    /// </summary>
    public static NetworkInterfaceInfo? PickTargetInterface(
        IReadOnlyList<NetworkInterfaceInfo> interfaces,
        ulong networkId,
        Func<NetworkInterfaceInfo, bool> isZeroTier)
    {
        if (interfaces.Count == 0)
        {
            return null;
        }

        string networkIdHex = networkId == 0
            ? string.Empty
            : networkId.ToString("x16", CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(networkIdHex))
        {
            NetworkInterfaceInfo? exact = interfaces.FirstOrDefault(
                i => isZeroTier(i)
                     && (i.Name.Contains(networkIdHex, StringComparison.OrdinalIgnoreCase)
                         || i.DisplayName.Contains(networkIdHex, StringComparison.OrdinalIgnoreCase)));

            if (exact is not null)
            {
                return exact;
            }
        }

        return interfaces.FirstOrDefault(isZeroTier);
    }

    /// <summary>
    /// 筛出「需要下调优先级」的候选：不是目标网卡、metric 与目标并列或更优、且是虚拟网卡。
    /// 并列的**实体网卡**收集到 <paramref name="physicalConflicts"/> 里回报（不改动，仅供日志/提示）。
    /// </summary>
    /// <param name="interfaces">扫描到的全部网卡（含 metric）。</param>
    /// <param name="target">已设为最高优先级的目标网卡（自身必须排除，否则会把自己下调）。</param>
    /// <param name="targetMetric">目标 metric（一般为 1）。</param>
    /// <param name="physicalConflicts">输出：并列但属于实体网卡的条目。</param>
    public static List<NetworkInterfaceInfo> SelectConflictCandidates(
        IReadOnlyList<NetworkInterfaceInfo> interfaces,
        NetworkInterfaceInfo target,
        int targetMetric,
        List<NetworkInterfaceInfo> physicalConflicts)
    {
        var candidates = new List<NetworkInterfaceInfo>();

        foreach (NetworkInterfaceInfo info in interfaces)
        {
            if (IsSameInterface(info, target) || !ConflictsWith(info.Metric, targetMetric))
            {
                continue;
            }

            if (IsVirtualInterface(info))
            {
                candidates.Add(info);
            }
            else
            {
                physicalConflicts.Add(info);
            }
        }

        return candidates;
    }

    /// <summary>判断两个描述是否指向同一块网卡：索引相同，或名字相同。</summary>
    public static bool IsSameInterface(NetworkInterfaceInfo a, NetworkInterfaceInfo b)
        => (a.IfIndex is int left && b.IfIndex is int right && left == right)
           || string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>把「已下调的网卡」和「未改动的并列实体网卡」拼成一句给界面/日志看的说明（无内容时返回空串）。</summary>
    public static string BuildConflictNote(IReadOnlyList<string> demoted, IReadOnlyList<string> physicalConflicts)
    {
        var note = new StringBuilder();

        if (demoted.Count > 0)
        {
            note.Append("；同时把并列最高优先级的虚拟网卡（")
                .Append(string.Join('、', demoted))
                .Append("）下调了一档，确保本网卡严格优先");
        }

        if (physicalConflicts.Count > 0)
        {
            note.Append("；实体网卡（")
                .Append(string.Join('、', physicalConflicts))
                .Append("）的 metric 同为最高，为不影响你的正常网络未做改动");
        }

        return note.ToString();
    }
}
