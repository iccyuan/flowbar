# FlowBar

Windows 任务栏网速监控。一个嵌在任务栏里的小组件，实时显示上传 / 下载速度；点开看曲线、今日 / 本月流量和各网卡速率；右键调整外观与位置。

- C# / WPF / .NET 10，零第三方依赖，单个 exe
- 嵌入任务栏（作为 `Shell_TrayWnd` 的子窗口），跟随任务栏显示 / 隐藏 / 全屏规避
  - Win10 式任务栏（含 StartAllBack、ExplorerPatcher）：缩短 `ReBarWindow32` 腾出位置，不遮挡任务按钮
  - Win11 原生任务栏：叠放在托盘图标左侧
  - 也可切换为可拖动的悬浮窗
- 透明背景，文字颜色按任务栏底色明暗自动选择（也可手动指定）
- 网卡自动选择：只统计物理网卡，排除 WFP / QoS / 杀软等过滤驱动镜像接口（否则会重复计数），可手动指定任一网卡（含 TUN / VPN）
- 单位：`12.3 KB/s`、紧凑 `12.3K`、比特 `98.4 Mb/s`
- 文字配色：自动 / 浅 / 深，另有 5 套上传 / 下载双色预设，按任务栏明暗自动取亮/暗变体
- 字体：任意系统字体 + 四档字重，对话框实时预览并即时应用
- 面板 / 菜单 / 提示 / 对话框均为快照式毛玻璃
- 自适应任务栏高度：两行 / 单行布局，字号自动
- 今日 / 本月累计流量持久化（`%AppData%\FlowBar\traffic.json`）
- 各程序流量与连接：按程序列出实时上下行、今日累计、连接数，表头可排序，同名进程折叠成一行、点击展开到每个 PID，顶部筛选框（Ctrl+F）按程序名 / 地址过滤；连接页同样按程序分组（连接数、汇总速率、已连接 / 监听数），展开后列出每条 TCP / UDP 连接的地址、状态和速率；统计页按今日 / 7 天 / 30 天看每日柱状图和程序排行，点某个程序只看它的每日流量
  - 连接列表无需特权；按程序统计网速用的是内核网络 ETW 事件（与任务管理器同源），需要管理员权限，窗口内一键以管理员身份重启
  - 按日、按程序累计持久化（`%AppData%\FlowBar\apps.json`）
- 开机自启（HKCU Run）、单实例、`FlowBar.exe --exit` 优雅退出并恢复任务栏

## 构建

```powershell
dotnet build FlowBar/FlowBar.csproj -c Release
# 单文件发布（依赖已安装的 .NET 10 桌面运行时）
dotnet publish FlowBar/FlowBar.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## 使用

运行 `FlowBar.exe`，组件出现在任务栏托盘图标左侧。

| 操作 | 效果 |
| --- | --- |
| 左键 | 详情面板：当前速率、最近 2 分钟曲线、今日 / 本月 / 本次流量、选择统计网卡 |
| 右键 | 菜单：各程序流量与连接、网卡、显示位置（靠托盘 / 靠左 / 悬浮）、布局、单位、字体与字号、文字颜色、刷新间隔、开机自启、退出 |
| 悬浮模式下左键拖动 | 移动位置（自动记住） |

配置文件：`%AppData%\FlowBar\config.json`，日志：`%AppData%\FlowBar\flowbar.log`。

调试：设置环境变量 `FLOWBAR_DIR`（或参数 `--dir <路径>`）可让实例使用独立配置目录（单实例互斥量与退出 / 唤起信号也随之隔离）；`--font` / `--popup` / `--menu` / `--apps` / `--apps=expand` / `--conns` / `--stats[=7|30]` 启动后直接打开字体对话框 / 详情面板 / 右键菜单 / 程序流量窗口（可全部展开）/ 连接页 / 统计页，便于无交互截图。 已有实例在运行时，再执行 `FlowBar.exe --apps` 会让它打开程序流量窗口。

## 代码结构

```
FlowBar/
  Core/Native.cs       P/Invoke（user32 / iphlpapi / gdi32 / dwmapi）
  Core/NetSampler.cs   GetIfTable2 采样、网卡筛选、速率与历史环
  Core/TaskbarHost.cs  嵌入任务栏：定位 Shell_TrayWnd、缩 ReBar、随 Explorer 重启自愈
  Core/TrafficLog.cs   按日累计流量
  Core/EtwNetMonitor.cs  Microsoft-Windows-Kernel-Network 实时 ETW 消费者：按进程 / 按连接计数（需管理员）
  Core/AppTraffic.cs   按程序汇总：速率、本次累计、连接数；每秒驱动
  Core/AppTrafficLog.cs  按日按程序累计
  Core/Connections.cs  GetExtendedTcpTable / GetExtendedUdpTable 连接表
  Core/ProcessInfo.cs  pid → 名称 / 路径 / 小图标
  Core/AppConfig.cs    配置 / 自启 / 日志
  Core/Theme.cs        明暗主题与任务栏底色采样
  Core/Formatting.cs   单位格式化
  UI/TaskbarWindow.cs  任务栏小组件（代码构建布局）+ 右键菜单
  UI/DetailPopup.xaml  详情面板
  UI/AppsWindow.xaml   各程序流量 / 连接 / 统计窗口（Mica 背景）
  Assets/              应用图标（app.ico、logo.png）
  App.xaml             菜单 / 按钮样式与调色板
  UI/FontDialog.xaml   字体 / 字重选择对话框
  Core/FrostedGlass.cs 快照式毛玻璃（截屏→模糊→按屏幕位置切片）
tools/test-ui.ps1      注入鼠标事件 + 截图的 UI 测试脚本
```

## 已知边界

- 多显示器只在主任务栏显示（`Shell_SecondaryTrayWnd` 未处理）。
- 竖向任务栏按两行布局横跨任务栏宽度，未细调。
- 文字颜色「自动」在启动和系统主题变化时采样一次任务栏底色；换壁纸导致的半透明任务栏明暗变化不会触发，可右键手动指定。
