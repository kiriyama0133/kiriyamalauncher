# kiriyamalauncher 项目长期笔记

## 项目定位
- .NET 10 + Avalonia UI 的游戏联机启动器，UI 组件库用 **SukiUI**（已升级到 7.0.2-nightly20260826.438）。
- 核心能力：ZeroTier（libzt 内嵌节点）虚拟局域网 + 游戏联机集成（目前只支持《文明 6》）。
- 三层架构：Presentation / Business / Data。

## 依赖版本（2026-09-15 升级）
- Avalonia 相关包：12.1.0 → **12.1.2**（Avalonia.Desktop / Controls.DataGrid / Fonts.Inter）。
- SukiUI：7.0.1 → **7.0.2-nightly20260826.438**（nightly，为获得 GlassCard 的位移+淡入入场动画）。
- ⚠️ 注意：**不要升到 7.0.2-nightly20260912.455 及更新版本**——9月11日起 SukiUI 把动画拆到独立包 `SukiUI.Motion`，该包未发布到 nuget.org，会导致 restore 报 NU1101 找不到包。
- 选择 7.0.2-nightly20260826.438 的原因：它是最后一个不含 SukiUI.Motion 依赖、且已带 GlassCard 入场动画（CompositionAnimationHelper 的 Offset 位移动画）的 nightly 版。

## SukiUI 组件参考（F:\SukiUI-main\SukiUI.Demo）
后续编写组件一律参照这个 Demo 的写法。关键约定：

- **命名空间**：Demo 用 `xmlns:suki="https://github.com/kikipoulet/SukiUI"`；
  本项目历史代码用 `clr-namespace:SukiUI.Controls;assembly=SukiUI`。两者等价，新代码建议跟 Demo 一致用 URI 形式（但需确认当前 SukiUI 版本支持）。
- **主题入口**：`<suki:SukiTheme ThemeColor="Blue" Locale="zh-CN" />`（App.axaml 里，本项目已用 zh-CN）。
- **主窗口**：`SukiWindow`，带 `Hosts`（SukiToastHost / SukiDialogHost）、`LogoContent`、`RightWindowTitleBarControls`、`MenuItems`。
- **导航**：`SukiSideMenu`（IsSearchEnabled、ItemTemplate 用 SukiSideMenuItem）。
- **背景**：`SukiBackground`（Style 可选 Bubble 等）。

## 常用组件（对照 Demo）
- **卡片**：`GlassCard`（Classes: Primary/Accent；IsOpaque/IsInteractive/IsAnimated）。
- **分组标题**：`GroupBox`（Header=...），配合 `Classes="HeaderCard"` 的 GlassCard 做页头。
- **按钮**：`Button` 用 Classes 切换样式：
  - 无/标准、`Basic`、`Flat`、`Outlined`、`Discrete`、`Rounded`、`Accent`、`Icon`、`Small`、`Large`、`Success`/`Information`/`Warning`/`Danger`。
  - 图标按钮用 `suki:ButtonExtensions.Icon="{avalonia:MaterialIconExt Kind=...}"`。
  - 加载态：`suki:ButtonExtensions.ShowProgress="True"`。
  - 无按压动画：项目里常用 `Classes="Basic NoPressedAnimation"`。
- **选择类**：RadioButton 的 `Chips`/`GigaChips`；ToggleButton 的 `Switch`/`CircleGlass` 等；ToggleSwitch（OnContent/OffContent）。
- **对话框**：C# API —— `ISukiDialogManager.CreateDialog().OfType(...).WithTitle(...).WithContent(...).WithActionButton(...).Dismiss().ByClickingBackground().TryShow()`；异步阻塞用 `TryShowAsync()` 拿结果。
- **消息框**：`SukiMessageBox.ShowDialog(new SukiMessageBoxHost{...})` / `ShowDialogResult(...)`；按钮工厂 `SukiMessageBoxButtonsFactory.CreateButton(...)`；图标 `SukiMessageBoxIcons`、按钮组 `SukiMessageBoxButtons`。
- **Toast**：`ISukiToastManager.CreateToast()/CreateSimpleInfoToast().WithTitle(...).WithContent(...).OfType(...).Dismiss().After(...)/ByClicking().Queue()`。
- **图标**：`Material.Icons.Avalonia` 的 `MaterialIcon`（Kind=...），Markup 扩展 `MaterialIconExt`。
- **动态资源**（颜色/字体都从 Suki 主题取）：`SukiText`、`SukiLowText`、`SukiPrimaryColor`、`SukiCardBackground`、`SukiBorderBrush`、`SukiPopupShadow`、`ShortAnimationDuration` 等。

## 本项目已确立的约定
- 字号不写死：用 `AppFontSizeSmall/Compact/Normal/Large/Subtitle/Title`（App.axaml Resources，运行时由 AppSnapshot 按用户偏好覆盖）。
- 中文界面，`Locale="zh-CN"`。
- 卡片悬停用「边线高亮」而非放大（GameSessionView 的 `Button.card` 样式）。
- 主窗口返回按钮 / 账号按钮在 `MainWindow.axaml` 的 Styles 里统一定义。
- MVVM：CommunityToolkit.Mvvm 风格（ObservableProperty / RelayCommand）。
- **卡片容器用 GlassCard**（`SukiUI.Controls.GlassCard`）：2026-09-15 起，boardCard/settingCard/integrationCard/devicesCard 已从 Border 换成 GlassCard，自动获得「位移+淡入」入场动画（IsAnimated 默认 true）。GlassCard 自带玻璃拟态背景+边框，样式里不用再设 Background/BorderBrush，只需设 CornerRadius/Padding/尺寸。
- GameCard（游戏卡片）仍用 Border + PART_Card 命名（被 GameSessionView 的 `Button.card c|GameCard Border#PART_Card` 选择器引用做悬停高亮，暂未迁移）。
- **游戏房间页面切换用 SukiTransitioningContentControl**（`SukiUI.Controls.SukiTransitioningContentControl`）：游戏房间「游戏列表 ↔ 联机面板」切换用它承载（纯 cross-fade 250ms，无面包屑）。⚠️ 曾用过 SukiStackPage 但因其内部栈状态在切换主导航页后错乱引发 bug，已弃用；页面对象（GameListPageViewModel/GameBoardPageViewModel）也不再实现 ISukiStackPageTitleProvider。内容通过 `Content="{Binding ActivePageContent}"` + DataTemplate 解析，页面对象只暴露协调器引用（`Session`），模板用 `Session.xxx` 路径绑定。

## ZeroTier 双后端架构（2026-09-15 起）
- `IZeroTierBackend : IZeroTierService`（Kind 标识），工厂 `ZeroTierBackendFactory` 按偏好 `ZeroTierTransportBackend`（Sockets/Client）选后端。
- **Sockets** = `ZeroTierService`（libzt 内嵌节点，无需虚拟网卡，配 civ6hook 注入）；**Client** = `ZeroTierClientBackend`（官方 MSI 静默安装 + `zerotier-one_x64.exe -q` 命令控制，出真虚拟网卡，联机面板自动隐藏注入 UI）。
- Client 后端模式：后台循环每 2s 刷 `listnetworks -j` 到不可变快照；**同步成员（GetStatus/LocalVirtualIp）只读快照**；命令 10s 硬超时；MSI 在 StartAsync 检测未装时自动静默安装。
- **铁律：接口同步成员绝不能在实现里跑子进程 / sync-over-async（GetResult/Wait），会冻死 UI 线程**（已发生过一次整窗死锁）；Data 层异步服务一律 `ConfigureAwait(false)`；子进程调用必须带超时。

## 环境备注
- 本项目工作目录的 bash 环境 PATH 有损坏（ls/dirname 找不到），浏览文件用 `/usr/bin/find`、`/usr/bin/ls` 全路径，或 Glob/Read 工具。
