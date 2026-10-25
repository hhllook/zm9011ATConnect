using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using Zm9011.Core;
using Zm9011.Core.Events;
using Zm9011.Modules.ModuleName;
using Zm9011.Modules.ModuleName.ViewModels;
using Zm9011.Services.Interfaces;
using Zm9011.Settings;

namespace Zm9011.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel：串口连接管理（含波特率自适应探测）、页面导航、
    /// 收发日志按“按钮动作模块”聚合、自定义 AT 指令发送、日志导出与清空。
    /// </summary>
    public class MainWindowViewModel : BindableBase
    {
        /// <summary>日志模块最大保留数量，超出后丢弃最旧的模块，防止长时间运行内存膨胀。</summary>
        private const int MaxLogSessions = 500;

        private readonly ISerialPortService _serial;
        private readonly IAtCommandService _at;
        private readonly ILogService _log;
        private readonly IRegionManager _regionManager;
        private readonly IEventAggregator _eventAggregator;

        /// <summary>动作 Id 到日志模块的索引，保证多命令动作（如短信、FOTA 分包）归入同一模块。</summary>
        private readonly Dictionary<string, LogSession> _sessionsById = new Dictionary<string, LogSession>();

        public MainWindowViewModel(ISerialPortService serial, IAtCommandService at, ILogService log,
            IRegionManager regionManager, IEventAggregator eventAggregator)
        {
            _serial = serial;
            _at = at;
            _log = log;
            _regionManager = regionManager;
            _eventAggregator = eventAggregator;

            Ports = new ObservableCollection<string>();
            LogSessions = new ObservableCollection<LogSession>();

            // 导航菜单：显示名 -> 视图名（名称面向使用场景，副标题说明具体能力）
            Pages = new ObservableCollection<NavItem>
            {
                new NavItem("设备信息与设置", ViewKeys.General, "查询设备信息、PIN、时钟与系统参数"),
                new NavItem("网络与信号", ViewKeys.Network, "注册状态、运营商、小区与频段锁定"),
                new NavItem("数据联网", ViewKeys.Data, "拨号、PDP 参数、流量与 IP 地址"),
                new NavItem("短信管理", ViewKeys.Sms, "短信模式、发送、读取、删除与通知设置"),
                new NavItem("实时短信", ViewKeys.RealtimeSms, "实时接收 · 本地保存 · 验证码识别"),
                new NavItem("固件升级", ViewKeys.Fota, "FOTA 升级、状态查询与升级包推送"),
                new NavItem("SIM 卡与扩展", ViewKeys.ZteExt, "SIM 卡信息、热插拔、双卡切换与 ADB"),
                new NavItem("TCP/IP 通信", ViewKeys.TcpIp, "TCP/UDP、FTP 与模组网关"),
                new NavItem("串口设置", ViewKeys.Uart, "串口参数、波特率自适应与探测")
            };

            _log.EntryLogged += OnLogEntry;
            _serial.ErrorOccurred += OnSerialError;
            _serial.Closed += OnSerialClosed;

            RefreshPortsCommand = new DelegateCommand(RefreshPorts);
            OpenCloseCommand = new DelegateCommand(async () => await OpenCloseAsync().ConfigureAwait(false));
            SendCustomCommand = new DelegateCommand(async () => await SendCustomAsync().ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(CustomCommand)).ObservesProperty(() => CustomCommand);
            ClearLogCommand = new DelegateCommand(ClearLog);
            SaveLogCommand = new DelegateCommand(SaveLog);

            // 初始选中"设备信息与设置"，与模块 OnInitialized 导航到 GeneralView 保持一致
            _currentPage = Pages[0];

            RefreshPorts();
        }

        /// <summary>可用串口名列表。</summary>
        public ObservableCollection<string> Ports { get; }

        /// <summary>收发日志模块集合（最新模块在最上方）。</summary>
        public ObservableCollection<LogSession> LogSessions { get; }

        /// <summary>导航菜单项。</summary>
        public ObservableCollection<NavItem> Pages { get; }

        private string _selectedPort = string.Empty;
        /// <summary>选中的串口名。</summary>
        public string SelectedPort { get => _selectedPort; set => SetProperty(ref _selectedPort, value); }

        private int _baudRate = 115200;
        /// <summary>串口波特率。</summary>
        public int BaudRate { get => _baudRate; set => SetProperty(ref _baudRate, value); }

        public int[] BaudRateOptions => UartViewModel.SupportedBaudRates;

        private bool _isOpen;
        /// <summary>串口是否已打开。</summary>
        public bool IsOpen { get => _isOpen; set
            {
                if (SetProperty(ref _isOpen, value))
                {
                    RaisePropertyChanged(nameof(ConnectionStatus));
                    RaisePropertyChanged(nameof(OpenButtonText));
                }
            }
        }

        /// <summary>连接状态描述（状态栏显示）。</summary>
        public string ConnectionStatus => IsOpen
            ? $"已连接 {_serial.CurrentPortName} @ {_serial.CurrentBaudRate}"
            : "未连接";

        /// <summary>打开/关闭按钮文本。</summary>
        public string OpenButtonText => IsOpen ? "断开" : "连接";

        private bool _autoDetect = true;
        /// <summary>打开失败或无响应时是否自动遍历波特率重试。</summary>
        public bool AutoDetect { get => _autoDetect; set => SetProperty(ref _autoDetect, value); }

        private string _customCommand = string.Empty;
        /// <summary>自定义 AT 指令输入框内容。</summary>
        public string CustomCommand { get => _customCommand; set => SetProperty(ref _customCommand, value); }

        private NavItem _currentPage = default!;
        /// <summary>当前导航页面。选中项变化时自动请求区域导航（左侧列表点击即切换页面）。</summary>
        public NavItem CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value) && value != null)
                {
                    Navigate(value);
                    RaisePropertyChanged(nameof(IsLogPanelVisible));
                }
            }
        }

        /// <summary>当前页是否需要显示底部收发日志/自定义指令区（实时短信页只显示业务界面）。</summary>
        public bool IsLogPanelVisible => CurrentPage?.ViewName != ViewKeys.RealtimeSms;

        public DelegateCommand RefreshPortsCommand { get; }
        public DelegateCommand OpenCloseCommand { get; }
        public DelegateCommand SendCustomCommand { get; }
        public DelegateCommand ClearLogCommand { get; }
        public DelegateCommand SaveLogCommand { get; }

        /// <summary>刷新本机串口列表。</summary>
        private void RefreshPorts()
        {
            var old = SelectedPort;
            Ports.Clear();
            foreach (var p in _serial.GetPortNames())
            {
                Ports.Add(p);
            }
            SelectedPort = Ports.Contains(old) ? old : Ports.FirstOrDefault() ?? string.Empty;
            _log.Info($"[MainWindowViewModel.RefreshPorts] 枚举到 {Ports.Count} 个串口");
        }

        /// <summary>打开/关闭串口。打开成功后发 AT 验证通信，失败时按开关自动探测波特率。</summary>
        private async Task OpenCloseAsync()
        {
            if (IsOpen)
            {
                _serial.Close();
                return;
            }

            if (string.IsNullOrEmpty(SelectedPort))
            {
                MessageBox.Show("请先选择串口", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _serial.Open(new SerialPortConfig
                {
                    PortName = SelectedPort,
                    BaudRate = BaudRate
                });
                IsOpen = true;

                // 发 AT 验证通信（超时短，仅探测）
                var probe = await _at.SendAsync("AT", 1000);
                if (probe.Success)
                {
                    _log.Info($"[MainWindowViewModel.OpenCloseAsync] 串口通信正常 port={SelectedPort} baud={BaudRate}");
                    SaveConnectionSettings();
                    PublishConnected();
                    Navigate(CurrentPage ?? Pages[0]);
                }
                else if (AutoDetect)
                {
                    await AutoDetectBaudRateAsync();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[MainWindowViewModel.OpenCloseAsync] 打开串口失败 port={SelectedPort} baud={BaudRate} error={ex.Message} stack={ex.StackTrace}");
                MessageBox.Show($"打开串口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 波特率自适应探测（对应手册第 10 章）：关闭当前串口后按支持列表
        /// 从高到低逐个重开并发 AT 试探，收到 OK 即锁定该波特率。
        /// </summary>
        /// <param name="silent">true 时不弹失败提示框（启动自动重连使用）。</param>
        public async Task AutoDetectBaudRateAsync(bool silent = false)
        {
            if (IsOpen)
            {
                _serial.Close();
            }
            IsOpen = false;

            _log.Info("[MainWindowViewModel.AutoDetectBaudRateAsync] 开始波特率自适应探测");
            foreach (var baud in UartViewModel.SupportedBaudRates.OrderByDescending(b => b))
            {
                try
                {
                    _serial.Open(new SerialPortConfig { PortName = SelectedPort, BaudRate = baud });
                    IsOpen = true;
                    BaudRate = baud;

                    var probe = await _at.SendAsync("AT", 800);
                    if (probe.Success)
                    {
                        _log.Info($"[MainWindowViewModel.AutoDetectBaudRateAsync] 探测成功 baud={baud}");
                        SaveConnectionSettings();
                        PublishConnected();
                        Navigate(CurrentPage ?? Pages[0]);
                        return;
                    }
                    _serial.Close();
                    IsOpen = false;
                }
                catch (Exception ex)
                {
                    // 该波特率打不开（驱动限制等），继续下一个
                    _log.Warn($"[MainWindowViewModel.AutoDetectBaudRateAsync] 波特率 {baud} 打开失败 error={ex.Message}");
                    IsOpen = false;
                }
            }

            _log.Error($"[MainWindowViewModel.AutoDetectBaudRateAsync] 波特率自适应失败 port={SelectedPort}，已尝试全部 {UartViewModel.SupportedBaudRates.Length} 档");
            if (!silent)
            {
                MessageBox.Show("波特率自适应失败：所有支持的波特率均无 AT 响应。\n请检查模组供电与接线。", "探测失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 启动时自动连接上一次成功连接的串口与波特率。
        /// 失败时静默记录日志，不阻塞启动；若允许自动探测则继续尝试其它波特率。
        /// </summary>
        public async Task AutoConnectLastPortAsync()
        {
            if (IsOpen)
            {
                return;
            }

            var settings = ConnectionSettingsStore.Load(_log);
            if (settings == null || string.IsNullOrWhiteSpace(settings.PortName))
            {
                return;
            }

            if (!Ports.Contains(settings.PortName))
            {
                _log.Warn($"[MainWindowViewModel.AutoConnectLastPortAsync] 上次连接的串口不存在 port={settings.PortName}");
                return;
            }

            SelectedPort = settings.PortName;
            BaudRate = settings.BaudRate;
            _log.Info($"[MainWindowViewModel.AutoConnectLastPortAsync] 自动连接上次串口 port={SelectedPort} baud={BaudRate}");

            try
            {
                _serial.Open(new SerialPortConfig { PortName = SelectedPort, BaudRate = BaudRate });
                IsOpen = true;

                var probe = await _at.SendAsync("AT", 1000);
                if (probe.Success)
                {
                    _log.Info($"[MainWindowViewModel.AutoConnectLastPortAsync] 自动连接成功 port={SelectedPort} baud={BaudRate}");
                    SaveConnectionSettings();
                    PublishConnected();
                    Navigate(CurrentPage ?? Pages[0]);
                    return;
                }

                if (AutoDetect)
                {
                    await AutoDetectBaudRateAsync(silent: true);
                }
                else
                {
                    _serial.Close();
                    IsOpen = false;
                    _log.Warn($"[MainWindowViewModel.AutoConnectLastPortAsync] 自动连接无 AT 响应 port={SelectedPort} baud={BaudRate}");
                }
            }
            catch (Exception ex)
            {
                IsOpen = false;
                _log.Warn($"[MainWindowViewModel.AutoConnectLastPortAsync] 自动连接失败 port={SelectedPort} baud={BaudRate} error={ex.Message} stack={ex.StackTrace}");
            }
        }

        /// <summary>保存本次成功连接参数，供下次启动自动重连。</summary>
        private void SaveConnectionSettings()
        {
            ConnectionSettingsStore.Save(new ConnectionSettings
            {
                PortName = _serial.CurrentPortName,
                BaudRate = _serial.CurrentBaudRate
            }, _log);
        }

        /// <summary>发布串口连接成功事件，触发实时短信自动开启。</summary>
        private void PublishConnected()
        {
            try
            {
                _eventAggregator.GetEvent<SerialConnectedEvent>().Publish();
            }
            catch (Exception ex)
            {
                _log.Error($"[MainWindowViewModel.PublishConnected] 发布连接成功事件失败 port={SelectedPort} error={ex.Message} stack={ex.StackTrace}");
            }
        }

        /// <summary>发送自定义 AT 指令。</summary>
        private async Task SendCustomAsync()
        {
            var cmd = CustomCommand.Trim();
            if (cmd.Length == 0)
            {
                return;
            }

            // Enter 键发送不会经过按钮点击，这里显式建立"自定义指令"动作模块
            AtActionScope.Current = new AtAction("自定义指令");

            try
            {
                // 自定义指令允许是任意 AT 命令（含交互式短信等），统一走标准队列
                await _at.SendAsync(cmd, 15000).ConfigureAwait(false);
                CustomCommand = string.Empty;
            }
            catch (Exception ex)
            {
                _log.Error($"[MainWindowViewModel.SendCustomAsync] 发送自定义指令失败 command={cmd} error={ex.Message} stack={ex.StackTrace}");
            }
        }

        /// <summary>导航到指定页面。</summary>
        private void Navigate(NavItem? page)
        {
            if (page == null)
            {
                return;
            }
            try
            {
                _regionManager.RequestNavigate(RegionNames.ContentRegion, page.ViewName);
            }
            catch (Exception ex)
            {
                _log.Error($"[MainWindowViewModel.Navigate] 页面导航失败 view={page.ViewName} error={ex.Message} stack={ex.StackTrace}");
            }
        }

        /// <summary>日志条目到达：调度到 UI 线程，按动作上下文归入对应模块。</summary>
        private void OnLogEntry(LogEntry entry)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            dispatcher.BeginInvoke(new Action(() => AppendLog(entry)));
        }

        /// <summary>把一条日志归入模块（必要时新建模块并置顶）。</summary>
        private void AppendLog(LogEntry entry)
        {
            var session = ResolveSession(entry);
            session.Add(entry.Level, entry.Message);
            UpdateStatus(session, entry);
        }

        /// <summary>按动作 Id 或日志类型解析目标模块。</summary>
        private LogSession ResolveSession(LogEntry entry)
        {
            // 1) 按钮动作产生的日志：同一次点击的多条命令共享 ActionId
            if (!string.IsNullOrEmpty(entry.ActionId))
            {
                if (_sessionsById.TryGetValue(entry.ActionId, out var existing))
                {
                    return existing;
                }

                var session = new LogSession(entry.ActionId, entry.ActionTitle ?? "AT 指令",
                    LogSessionKind.Action, entry.Time);
                AddSession(session);
                _sessionsById[entry.ActionId] = session;
                return session;
            }

            // 2) 无动作上下文的日志：主动上报 / 透传数据 / 系统信息
            return entry.Level switch
            {
                LogLevel.Urc => GetOrCreateTopSession(LogSessionKind.Urc, "主动上报", entry),
                LogLevel.Rx or LogLevel.Tx => GetOrCreateTopSession(LogSessionKind.Data, "透传数据", entry),
                _ => GetOrCreateTopSession(LogSessionKind.System, "系统信息", entry)
            };
        }

        /// <summary>顶部模块类型一致时合并，否则新建模块并置顶。</summary>
        private LogSession GetOrCreateTopSession(LogSessionKind kind, string title, LogEntry entry)
        {
            var top = LogSessions.FirstOrDefault();
            if (top != null && top.Kind == kind)
            {
                return top;
            }

            var session = new LogSession(Guid.NewGuid().ToString("N"), title, kind, entry.Time);
            AddSession(session);
            return session;
        }

        /// <summary>插入最新模块并从尾部裁剪超限模块。</summary>
        private void AddSession(LogSession session)
        {
            LogSessions.Insert(0, session);
            while (LogSessions.Count > MaxLogSessions)
            {
                var tail = LogSessions[LogSessions.Count - 1];
                LogSessions.RemoveAt(LogSessions.Count - 1);
                _sessionsById.Remove(tail.Id);
            }
        }

        /// <summary>根据终止符或超时/错误日志更新模块执行状态。</summary>
        private static void UpdateStatus(LogSession session, LogEntry entry)
        {
            if (session.Kind != LogSessionKind.Action)
            {
                return;
            }

            if (entry.Level == LogLevel.Rx)
            {
                if (entry.Message == "OK" || entry.Message == "CONNECT")
                {
                    session.SetStatus("成功");
                }
                else if (entry.Message == "ERROR"
                    || entry.Message.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase)
                    || entry.Message.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase)
                    || entry.Message.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    session.SetStatus("失败");
                }
            }
            else if (entry.Level == LogLevel.Warn && entry.Message.Contains("响应超时"))
            {
                session.SetStatus("超时");
            }
            else if (entry.Level == LogLevel.Error)
            {
                session.SetStatus("失败");
            }
        }

        /// <summary>清空日志模块。</summary>
        private void ClearLog()
        {
            LogSessions.Clear();
            _sessionsById.Clear();
        }

        /// <summary>串口错误：记录日志（会自动归入当前动作模块）。</summary>
        private void OnSerialError(object? sender, Exception ex)
        {
            _log.Error($"[MainWindowViewModel.OnSerialError] 串口错误 error={ex.Message} stack={ex.StackTrace}");
        }

        /// <summary>串口关闭（含意外拔出）：复位连接状态。</summary>
        private void OnSerialClosed(object? sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                IsOpen = false;
                return;
            }
            dispatcher.BeginInvoke(() =>
            {
                if (IsOpen)
                {
                    _log.Warn("[MainWindowViewModel.OnSerialClosed] 串口连接已断开");
                }
                IsOpen = false;
            });
        }

        /// <summary>导出日志模块到文本文件（按时间正序）。</summary>
        private void SaveLog()
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "文本文件|*.log;*.txt",
                    FileName = $"Zm9011Log_{DateTime.Now:yyyyMMdd_HHmmss}.log"
                };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var lines = new List<string>();
                foreach (var session in LogSessions.Reverse())
                {
                    var status = string.IsNullOrEmpty(session.Status) ? string.Empty : $" [{session.Status}]";
                    lines.Add($"===== {session.StartedAt:yyyy-MM-dd HH:mm:ss}  {session.Title}{status} =====");
                    foreach (var line in session.Lines)
                    {
                        lines.Add($"    {line.DisplayText}");
                    }
                    if (session.IsTruncated)
                    {
                        lines.Add($"    {session.TruncatedText}");
                    }
                    lines.Add(string.Empty);
                }

                System.IO.File.WriteAllLines(dialog.FileName, lines);
                _log.Info($"[MainWindowViewModel.SaveLog] 日志已导出 file={dialog.FileName} sessions={LogSessions.Count}");
            }
            catch (Exception ex)
            {
                _log.Error($"[MainWindowViewModel.SaveLog] 日志导出失败 error={ex.Message} stack={ex.StackTrace}");
                MessageBox.Show($"日志导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>导航菜单项。</summary>
    public class NavItem
    {
        public NavItem(string displayName, string viewName, string? subtitle = null)
        {
            DisplayName = displayName;
            ViewName = viewName;
            Subtitle = subtitle ?? "指令发送结果实时显示在下方收发日志";
        }

        /// <summary>菜单显示名。</summary>
        public string DisplayName { get; }

        /// <summary>Prism 导航视图名。</summary>
        public string ViewName { get; }

        /// <summary>页面标题右侧的说明文字。</summary>
        public string Subtitle { get; }
    }
}
