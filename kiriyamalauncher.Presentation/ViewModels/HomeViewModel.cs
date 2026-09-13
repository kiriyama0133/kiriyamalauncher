using kiriyamalauncher.Presentation.Base;
using Material.Icons;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;

namespace kiriyamalauncher.Presentation.ViewModels;

public partial class HomeViewModel : PageViewModel
{
    public override string DisplayName => "首页";

    public override MaterialIconKind Icon => MaterialIconKind.Home;

    public string AppDisplayName { get; }

    public string AppDescription { get; }

    public string ApplicationInfo { get; }

    public string LicenseURL { get; }

    public string Purpose { get; }

    public string HowItWorks { get; }

    public string Highlights { get; }

    public string Status { get; }

    public HomeViewModel()
    {
        AppDisplayName = AppInfo.AppDisplayName;
        AppDescription = AppInfo.AppDescription;
        ApplicationInfo = $"{AppInfo.AppDisplayName} {AppInfo.Version}";
        LicenseURL = AppInfo.LicenseURL;

        Purpose = "Kiriyama Launcher 是一个给玩家用的联机工具：它把 ZeroTier 的点对点组网能力直接内置进程序，" +
                  "让你和朋友像待在同一个局域网里一样开黑，而不用先折腾网络配置。";

        HowItWorks = "程序内置了 ZeroTier 节点（libzt），在游戏里点一下「连接到虚拟局域网」，成员之间就会自动建立加密的点对点连接。" +
                     "整个过程不需要另外安装 ZeroTier 客户端，也不会新增虚拟网卡驱动；联机需要放行的端口，由程序按当前平台自动处理。";

        Highlights =
            "• 开箱即用：无需安装 ZeroTier 客户端，也不会多出一块虚拟网卡。\n" +
            "• 点对点直连：成员之间自动协商直连通道，不依赖中转服务器转发游戏流量。\n" +
            "• 加密通信：链路加密由 ZeroTier 负责，只有同一个虚拟局域网里的成员互相可见。\n" +
            "• 一键开黑：把组网、防火墙放行和启动游戏收拢在同一个界面里。";

        Status = "功能仍在开发中：当前版本完成了界面骨架与内嵌 ZeroTier 组网，" +
                 "「连接到虚拟局域网」已经能把本机接入你配置的 ZeroTier 网络。";
    }
}
