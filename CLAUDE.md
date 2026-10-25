# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

中兴通讯 ZM9011 模组 AT 指令调试软件。WPF + Prism.DryIoc 8.1.97，目标框架 net6.0-windows。
指令规范依据 `AT指令手册\中兴通讯_ZM9011_模组AT指令手册.txt`（V3.9，含各章节语法结构与附录 A/B/C 错误码表）。

## 常用命令

```bash
# 构建整个解决方案（slnx 格式）
dotnet build Zm9011.slnx

# 运行（无测试工程，Tests 目录已移除）
Zm9011\Zm9011\bin\Debug\net6.0-windows\Zm9011.exe
```

无 lint 配置。已知警告：NETSDK1138（net6.0-windows EOL 提示，模板遗留，可忽略）。

## 架构

```
Zm9011.slnx
├── Zm9011.Services.Interfaces   接口：ISerialPortService / IAtCommandService / ILogService（net6.0）
├── Zm9011.Services              实现：SerialPortService / AtCommandService / LogService（net6.0 + System.IO.Ports）
├── Zm9011.Core                  RegionNames、ViewModelBase、RegionViewModelBase、SerialConnectedEvent
├── Zm9011.Modules.ModuleName    AT 调试页面模块（目录名沿用模板，实际为 AtPagesModule）
└── Zm9011                       主程序：MainWindow + App（DI 注册）
```

依赖方向：主工程 → 模块 → Core → Services.Interfaces；Services 实现层只被主工程在 DI 注册时引用。

### AT 指令核心流程（AtCommandService，最重要）

- **串行队列**：AT 串口半双工，`SendAsync` 入队后由 `PumpQueueAsync`（SemaphoreSlim 串行化）逐条发送，`_current` 指向正在收集响应的命令。
- **按行解析**：串口字节 → `_lineBuffer` 拆行（`TryTakeLine` 处理 \r\n）→ `ProcessLine` 分类：回显丢弃 / `>` 提示符 / 触发行 / 终止符 / URC / 普通响应。
- **终止符**：`OK`、`ERROR`、`+CME ERROR`、`+CMS ERROR`、`ERROR:`、`CONNECT`（`IsFinalLine`）。
- **两段式命令**：`AT+CMGS`（短信）、`AT+MYNETWRITE`、`AT+MYFTPGET/PUT` 等需要"命令 → 触发行 → 正文 → 最终响应"。用 `SendInteractiveAsync(command, triggerPrefix, body, timeout, connectIsFinal)`：
  - `triggerPrefix=">"`：收到提示符后由 `SendBodyAsync` 发正文并以 Ctrl-Z (0x1A) 结束（仅短信类）。
  - `triggerPrefix="CONNECT"`：`connectIsFinal=true` 时 CONNECT 即终止（透传 MYNETCREATE）；false 时 CONNECT 仅为触发行，继续等 OK（FTPGET/FTPPUT）。
- **URC 白名单**：`UrcPrefixes` 只含明确的无请求上报（+CMTI/+MYURCREAD/+ZIND 等）。**注意 +CREG/+CEREG/+CGREG 故意不在表内**——它们既是查询响应也是主动上报，放进去会吞掉查询响应；空闲期由 `LineReceived` 事件分发。
- **错误翻译**：`DescribeError` 把附录 A（900/9xx 扩展+TCP 错误）、附录 B（CME）、附录 C（CMS）错误码翻成中文描述。

### 页面导航（易错点）

左侧导航列表绑定 `MainWindowViewModel.CurrentPage`，**导航逻辑在 CurrentPage 的 setter 里**（选中变化 → `RequestNavigate(ContentRegion, viewName)`）。不要改成 Command 方式，`Navigate` 内部不要再回写 `CurrentPage`（会递归触发）。

模块 `AtPagesModule` 注册 9 个视图（GeneralView/NetworkView/DataView/SmsView/RealtimeSmsView/FotaView/ZteExtView/TcpIpView/UartView），视图名常量在 `ViewKeys`。所有页面用 `prism:ViewModelLocator.AutoWireViewModel`，ViewModel 必须放在 `Zm9011.Modules.ModuleName.ViewModels` 命名空间（Prism 按约定解析）。

**实时短信页只显示业务界面**：`MainWindowViewModel.IsLogPanelVisible` 为 false 时，`MainWindow` code-behind 把底部日志行高设为 0 并隐藏分隔条/日志面板；离开该页恢复用户拖动后的高度。注意 Prism 在 `InitializeComponent()` 内就会设置 DataContext，`MainWindow` 构造函数需在 `InitializeComponent()` 之后主动 `AttachViewModel()`，不能只依赖 `DataContextChanged`。

### 串口自动重连与实时短信

- **上次连接参数**：成功建立 AT 通信后写入 `%LOCALAPPDATA%\Zm9011\settings.json`（`ConnectionSettingsStore`）；主窗口 `Loaded` 时调用 `AutoConnectLastPortAsync()` 自动重连，失败静默记录日志（`AutoDetectBaudRateAsync(silent:true)`），不弹窗阻塞启动。
- **连接成功事件**：`MainWindowViewModel` 在 AT 探测成功后发布 `SerialConnectedEvent`（Core/Events）。
- **实时短信服务**：`RealtimeSmsService` 在 `AtPagesModule.OnInitialized` 中被主动解析为常驻单例。它订阅 `+CMTI` URC 与 `SerialConnectedEvent`，连接成功后自动发送 `AT+CMGF=0` + `AT+CNMI=2,1,0,0,0` 开启实时接收；因此离开实时短信页后仍能收短信。
- **接收流程**：`+CMTI` → `AT+CMGR=<index>` → `SmsPduDecoder`（PDU 优先，兼容 TEXT）→ 识别验证码 → 保存到 `%LOCALAPPDATA%\Zm9011\received_sms.json` → `MessageReceived` 事件通知页面。
- **页面 ViewModel**：`RealtimeSmsViewModel` 是瞬时对象，只负责会话聚合与交互；`Attach/Detach` 管理对 `MessageReceived` 的订阅（视图 `Unloaded`/`IDestructible` 会解绑），避免导航切换后旧页面重复更新。
- **发送回复**：临时切到 `AT+CMGF=1`，中文自动 `AT+CSMP=17,255,0,8` + UCS2 HEX，发完恢复 `AT+CMGF=0`，保证后续 +CMTI 仍按 PDU 读取。

### 线程与日志

- 日志按“按钮动作模块”展示：`AtActionTracker`（附加行为，挂在按钮样式上）在点击时把按钮功能名写入 `AtActionScope.Current`；`AtCommandService` 在命令入队时捕获该 `AtAction`，并把它附加到本次命令产生的 Tx/Rx 日志（`LogEntry.ActionId/ActionTitle`）。
- `MainWindowViewModel.LogSessions` 按 `ActionId` 聚合日志模块；**最新模块插入索引 0（最新在上）**，同一次点击的多条命令（如 +CCLK? 前的 +ZNITZ?、短信 +CSMP/+CMGS）归入同一模块。URC 与系统日志单独成组（`主动上报` / `系统信息`），每个模块最多保留 400 行。
- `ILogService.EntryLogged` 从后台线程触发，订阅方需调度到 Dispatcher；`LogSessions`/`LogSession.Lines` 只在 UI 线程写入。
- 所有错误日志格式遵循用户全局规范：`[类.方法] 关键参数=值 error=... stack=...`（中文注释、中文日志）。

## 约定与陷阱

- **模块目录名 ≠ 模块类名**：目录/工程仍叫 `Zm9011.Modules.ModuleName`（模板遗留，重命名目录会被 VS 占用），模块类为 `AtPagesModule`，命名空间统一 `Zm9011.Modules.ModuleName`。新建文件沿用此命名空间。
- **Services 工程是 net6.0**（非 net6.0-windows），已设置 `SuppressTfmSupportBuildWarnings=true` 抑制 System.IO.Ports 10.0.12 对 net6.0 的兼容性警告。
- **波特率列表**唯一来源：`UartViewModel.SupportedBaudRates`（手册 9.1.3 的 12 档），主窗口波特率探测（`AutoDetectBaudRateAsync`）也从高到低遍历它。
- **`+++` 退出透传**需前后 1 秒静默期（手册 2.11），实现里用 `Task.Delay(1000)` + `SendRaw("+++")`，不走 AT 队列。
- **中文短信 TEXT 模式**：先 `AT+CSMP=17,255,0,8`（UCS2），正文转 UCS2 HEX（`ToUcs2Hex`），发完 `AT+CSMP=,,0,0` 恢复默认（手册 5.2.4）。
- **FOTA 分包推送**（手册 6.10）：set → mid/end 分包（USB ≤512 字节、UART ≤1024 字节 HEX）→ check；模组路径必须 `/zterw/cache/` 开头。
