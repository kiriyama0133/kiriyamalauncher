civ6hook-diag v2 —— 文明 6 联机 Hook「只记录不转发」诊断包
================================================================

这个目录里是我们自己写的诊断版 Hook DLL（x64）。它只记录日志，不做任何转发，
用来确认游戏的网络调用方式：谁在发包、发到哪、谁在收包、有没有真的收到数据。

文件说明
  build\hookdll.dll            编译产物（.NET 启动器 / 注入器实际加载的就是这份）
  diag.cpp                     源码
  build-diag.ps1               编译脚本（VS 的 cl.exe，/MT 静态 CRT，不依赖运行库）
  install-diag.ps1             把编译产物 + 脚本装进 kit 目录
  test-load.ps1                自检：把 DLL 加载进一个临时 PowerShell，做一次广播发送+收包，
                               验证 Hook 是否正常工作（不碰游戏）
  launcher-hook.ps1            把诊断版装进启动器的自动注入路径（Scripts\civ6\hookdll.dll），
                               -Restore 可还原原版
  run-diag.ps1                 手动一键诊断：检测游戏进程 → 提权注入 → 显示日志
  tunnel-pipe-test.ps1         隧道管道自测（测试 launcher 侧命名管道）
  kit\                         打包出去的一整套（DLL + 脚本 + 注入器）

推荐流程
  0. 改完代码先跑：  .\build-diag.ps1
  1. 自检：          .\test-load.ps1            （应当看到一条 ★sendto dst=255.255.255.255:62999）
  2. 装进启动器：    .\launcher-hook.ps1        （备份原版 → 换成诊断版）
  3. 用启动器启动文明 6，进「多人游戏 → 局域网」，点几次刷新房间列表，玩 30 秒
  4. 退出游戏，把日志发我：
       %LOCALAPPDATA%\kiriyamalauncher\logs\civ6-hook-diag-<日期时间>.log
       %LOCALAPPDATA%\kiriyamalauncher\logs\civ6-hook-diag-summary.txt（同名摘要，更短）
  5. 想恢复原版：    .\launcher-hook.ps1 -Restore

日志里会记录
  - 每次调用的【调用者模块】（例如 GameCore_Base_FinalRelease.dll+0x1234）—— 这条最重要，
    能区分是游戏本体、EOS SDK 还是 Steam 在收发。
  - socket / WSASocketW：谁创建了哪个 socket（协议、类型）
  - bind / getsockname：socket 绑到了哪个端口（62900-62999 会打 ★）
  - setsockopt(SO_BROADCAST)：有没有打开广播
  - sendto / WSASendTo / send：目标 IP:端口、长度、返回值、前 16 字节内容
    （目标是广播地址或 62900-62999 端口时会打 ★）
  - recvfrom / WSARecvFrom / recv / WSARecv：返回值、收到多少字节、来源、前 16 字节内容
  - select / WSAPoll / WSAEventSelect / WSAEnumNetworkEvents / IOCP：等待可读用的是哪一套
  - GetProcAddress 动态解析：谁在运行时解析 sendto 这类函数（这种写法 IAT Hook 抓不到）
  - 延迟导入表(/DELAYLOAD) 与「按序号导入」也会被补挂
  - 「本进程端口快照」：每 3 秒问一次系统，本进程到底绑了哪些 UDP/TCP 端口
    （这条完全不依赖 Hook，是判断「游戏有没有开 62900-62999 局域网端口」的铁证）
  - 「首次见到 sock=X」：第一次见到某个 socket 时，主动调用真正的 getsockname
    查出它绑在哪个端口，避免我们漏掉它的创建调用
  - 摘要文件里还有「按 socket 汇总」的表格：发送/接收次数、绑定端口、创建者、首包内容

注意
  - 诊断版不补发局域网广播，所以跨 ZeroTier 联机在诊断期间是搜不到房间的（预期）。
  - 它 Hook 了 GetProcAddress，稳定但会稍微拖慢动态解析，只适合诊断用。
  - 原版 hookdll.dll 会被备份成 hookdll.original.dll，随时能还原。
