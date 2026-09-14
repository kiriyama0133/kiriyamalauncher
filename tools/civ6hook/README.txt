civ6hook —— 文明 6 局域网联机的「转发版」Hook（配合启动器的 ZeroTier 隧道）
================================================================

它解决的问题：文明 6 的局域网联机完全依赖「广播找人」——
游戏会往 255.255.255.255:62900-62999 连发 100 个 4 字节广播；
房主则在 0.0.0.0:62900 上监听。广播跨不了互联网，所以在不同网络里的两台机器
永远互相看不到。这个 Hook 把「广播」变成「点对点 UDP」，让两台机器在 ZeroTier
虚拟局域网里就像在同一台路由器下。

实测依据（来自 ..\civ6hook-diag 的日志）
  找房间：socket() UDP → sendto(255.255.255.255, 62900..62999) ×100，4 字节，
          源端口是临时端口，socket 约 2 秒后关闭（caller +0x85D698 / +0x85D5E3）
  当房主：bind(0.0.0.0:62900) 收探测（+0x85D4F3），另有 bind(0.0.0.0:<临时端口>)
          的会话 socket，用非阻塞 WSARecvFrom 疯狂轮询（+0xC73450）

工作方式
  出站：凡是发往「广播地址」或「端口 62900-62999」或「对端虚拟 IP」的 UDP，
        额外组一帧（本地源端口 + 目标端口 + 报文）写进命名管道交给启动器；
        启动器用内嵌 ZeroTier 在同一个本地端口上单播给对端。
        同时**照原样放行**，所以同一局域网内正常联机不受影响。
  入站：启动器从虚拟网收到的包，按「本地目标端口」写回管道；Hook 用回环 UDP
        把包裹投进游戏那个 socket（前面加 14 字节标记，游戏读出来时剥掉并把
        from 改写成对端虚拟 IP:端口）。这样阻塞收包、非阻塞轮询、select/WSAPoll
        三种等待方式都能正常拿到数据。

文件说明
  civ6hook.cpp       源码
  build-hook.ps1     编译（cl.exe，/MT 静态 CRT）
  test-hook.ps1      不上游戏的自测：本机模拟启动器，验证「出站上报 + 入站注入」整条链路
  deploy-hook.ps1    装进启动器自动注入路径（-Diag 装诊断版，-Restore 还原原版）
  build\hookdll.dll  编译产物

环境变量
  KIRIYAMA_LAN_DISABLE=1   完全不启用转发（等于没注入）
  KIRIYAMA_LAN_DRYRUN=1    只记录、不转发也不注入
  KIRIYAMA_LAN_VERBOSE=1   详细日志
  KIRIYAMA_LAN_PIPE=<名字> 换一个管道名（测试用）

日志位置
  DLL 同目录：civ6hook-<日期时间>.log
  %LOCALAPPDATA%\kiriyamalauncher\logs\civ6hook-<日期时间>.log

典型用法
  1. .\build-hook.ps1
  2. .\test-hook.ps1            （应当看到 出站帧 + “收到注入数据=HELLO-FROM-PEER 来自=...”）
  3. .\deploy-hook.ps1 -Publish （装进启动器；原版会备份成 hookdll.original.dll）
  4. 打开启动器 → 连上虚拟局域网 → 房间里开启「游戏隧道」，对端选另一台机器
  5. 两台都启动游戏 → 多人游戏 → 局域网 → 一台创建房间，另一台刷新列表
  6. 想还原：.\deploy-hook.ps1 -Restore -Publish
