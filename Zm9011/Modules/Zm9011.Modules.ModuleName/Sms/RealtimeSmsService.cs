using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Prism.Events;
using Prism.Mvvm;
using Zm9011.Core.Events;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.Sms
{
    /// <summary>
    /// 实时短信常驻服务（单例）：监听 +CMTI、读取并解析 PDU、保存到本地，
    /// 串口连接成功后自动开启 +CNMI 主动上报。与页面生命周期解耦，
    /// 离开实时短信页后仍能持续接收。
    /// </summary>
    public class RealtimeSmsService : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ISerialPortService _serial;
        private readonly ILogService _log;

        /// <summary>串口读取串行锁：避免多条 +CMTI 同时触发读取时结果交叉。</summary>
        private readonly SemaphoreSlim _readLock = new SemaphoreSlim(1, 1);

        /// <summary>本地消息集合（加锁访问）。</summary>
        private readonly object _messagesLock = new object();
        private readonly List<SmsMessage> _messages = new List<SmsMessage>();

        public RealtimeSmsService(IAtCommandService at, ISerialPortService serial, ILogService log, IEventAggregator eventAggregator)
        {
            _at = at;
            _serial = serial;
            _log = log;

            _at.UrcReceived += OnUrcReceived;
            // 串口连接成功后自动开启实时接收（发布线程不固定，内部会调度到 UI 线程）
            eventAggregator.GetEvent<SerialConnectedEvent>().Subscribe(OnSerialConnected, ThreadOption.PublisherThread, true);
            // 启动即创建并加载本地历史，保证页面未打开时也能接收与持久化
            LoadStoredMessages();
        }

        /// <summary>新消息（接收或发送）事件。在 UI 线程触发。</summary>
        public event Action<SmsMessage>? MessageReceived;

        private string _statusText = "串口未连接，请先在顶部连接串口";
        /// <summary>实时接收状态提示。</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        private bool _isRealtimeEnabled;
        /// <summary>实时接收是否已启用。</summary>
        public bool IsRealtimeEnabled
        {
            get => _isRealtimeEnabled;
            private set => SetProperty(ref _isRealtimeEnabled, value);
        }

        private bool _deleteAfterRead = true;
        /// <summary>读取成功后是否删除模组侧短信（避免存储占满）。</summary>
        public bool DeleteAfterRead
        {
            get => _deleteAfterRead;
            set => SetProperty(ref _deleteAfterRead, value);
        }

        private string _simSlotText = string.Empty;
        /// <summary>当前 SIM 卡槽徽标，如 "SIM 2"；未知时为空。</summary>
        public string SimSlotText
        {
            get => _simSlotText;
            private set
            {
                if (SetProperty(ref _simSlotText, value))
                {
                    RaisePropertyChanged(nameof(HasSimSlot));
                }
            }
        }

        public bool HasSimSlot => !string.IsNullOrEmpty(SimSlotText);

        /// <summary>由页面写入一条用户可见的状态提示。</summary>
        public void SetStatusForUser(string status)
        {
            SetStatusSafe(status);
        }

        /// <summary>获取本地消息快照（按加入顺序）。</summary>
        public IReadOnlyList<SmsMessage> GetMessagesSnapshot()
        {
            lock (_messagesLock)
            {
                return _messages.ToList();
            }
        }

        /// <summary>保存全部消息到本地。</summary>
        public void SaveAll()
        {
            List<SmsMessage> snapshot;
            lock (_messagesLock)
            {
                snapshot = _messages.ToList();
            }
            SmsStore.Save(snapshot, _log);
        }

        /// <summary>删除指定 Id 的消息并保存。</summary>
        public void RemoveMessages(IEnumerable<string> ids)
        {
            var idSet = new HashSet<string>(ids);
            lock (_messagesLock)
            {
                _messages.RemoveAll(m => idSet.Contains(m.Id));
            }
            SaveAll();
        }

        /// <summary>清空全部本地消息。</summary>
        public void ClearAll()
        {
            lock (_messagesLock)
            {
                _messages.Clear();
            }
            SaveAll();
        }

        /// <summary>串口连接成功后调用；已开启时幂等返回。</summary>
        public async Task EnsureEnabledAsync()
        {
            if (!_serial.IsOpen)
            {
                StatusText = "串口未连接，请先在顶部连接串口";
                IsRealtimeEnabled = false;
                return;
            }

            if (IsRealtimeEnabled)
            {
                return;
            }

            await EnableRealtimeAsync();
        }

        /// <summary>发送回复短信（中文自动转 UCS2），成功后加入本地并触发事件。</summary>
        public async Task<bool> SendReplyAsync(string sender, string text)
        {
            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!_serial.IsOpen)
            {
                StatusText = "串口未连接，请先在顶部连接串口";
                return false;
            }

            try
            {
                AtActionScope.Current = new AtAction($"回复 {sender}");

                var mode = await _at.SendAsync("AT+CMGF=1", 5000);
                if (!mode.Success)
                {
                    StatusText = "切换到 TEXT 模式失败";
                    _log.Error($"[RealtimeSmsService.SendReplyAsync] 切换短信模式失败 to={sender} final={mode.FinalLine}");
                    return false;
                }

                var useUcs2 = text.Any(ch => ch > 0x7F);
                if (useUcs2)
                {
                    var csmp = await _at.SendAsync("AT+CSMP=17,255,0,8", 5000);
                    if (!csmp.Success)
                    {
                        StatusText = "设置 UCS2 编码失败";
                        _log.Error($"[RealtimeSmsService.SendReplyAsync] 设置 UCS2 编码失败 to={sender} final={csmp.FinalLine}");
                        return false;
                    }
                }

                var body = useUcs2 ? ToUcs2Hex(text) : text;
                var result = await _at.SendInteractiveAsync($"AT+CMGS=\"{sender}\"", ">", body, 20000);

                if (useUcs2)
                {
                    await _at.SendAsync("AT+CSMP=,,0,0", 5000);
                }

                // 恢复 PDU 模式，保证后续 +CMTI 读取使用 PDU
                await _at.SendAsync("AT+CMGF=0", 5000);

                if (!result.Success)
                {
                    StatusText = "发送失败：" + (string.IsNullOrEmpty(result.ErrorMessage) ? result.FinalLine : result.ErrorMessage);
                    _log.Error($"[RealtimeSmsService.SendReplyAsync] 发送短信失败 to={sender} final={result.FinalLine} error={result.ErrorMessage}");
                    return false;
                }

                var message = new SmsMessage
                {
                    Sender = sender,
                    Body = text,
                    ReceivedAt = DateTime.Now,
                    IsOutgoing = true,
                    IsRead = true,
                    RawText = text
                };

                AddMessage(message);
                StatusText = "短信已发送";
                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"发送异常：{ex.Message}";
                _log.Error($"[RealtimeSmsService.SendReplyAsync] 发送短信异常 to={sender} error={ex.Message} stack={ex.StackTrace}");
                return false;
            }
        }

        /// <summary>串口连接成功事件：调度到 UI 线程后自动开启实时接收。</summary>
        private void OnSerialConnected()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                _ = EnsureEnabledAsync();
                return;
            }

            dispatcher.BeginInvoke(new Action(() => _ = EnsureEnabledAsync()));
        }

        /// <summary>加载本地历史短信。</summary>
        private void LoadStoredMessages()
        {
            var stored = SmsStore.Load(_log);
            lock (_messagesLock)
            {
                _messages.AddRange(stored.OrderBy(m => m.ReceivedAt));
            }
        }

        /// <summary>URC 到达：识别 +CMTI 并读取对应索引的短信。</summary>
        private void OnUrcReceived(string line)
        {
            if (string.IsNullOrWhiteSpace(line)
                || !line.StartsWith("+CMTI:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var match = Regex.Match(line, @"^\+CMTI:\s*""([^""]+)""\s*,\s*(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                _log.Warn($"[RealtimeSmsService.OnUrcReceived] +CMTI 格式无法解析 line={line}");
                return;
            }

            var storage = match.Groups[1].Value;
            if (!int.TryParse(match.Groups[2].Value, out var index))
            {
                _log.Warn($"[RealtimeSmsService.OnUrcReceived] +CMTI 索引非法 line={line}");
                return;
            }

            _ = Task.Run(() => ReadSmsAsync(storage, index));
        }

        /// <summary>读取指定索引短信（PDU 优先），解析后保存并通知界面。</summary>
        private async Task ReadSmsAsync(string storage, int index)
        {
            await _readLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var action = new AtAction("接收短信");
                AtActionScope.Current = action;

                var result = await _at.SendAsync($"AT+CMGR={index}", 15000).ConfigureAwait(false);
                if (!result.Success)
                {
                    _log.Error($"[RealtimeSmsService.ReadSmsAsync] 读取短信失败 storage={storage} index={index} final={result.FinalLine} error={result.ErrorMessage}");
                    SetStatusSafe("读取短信失败");
                    return;
                }

                var message = ParseCmgr(result, storage, index);
                if (message == null)
                {
                    _log.Warn($"[RealtimeSmsService.ReadSmsAsync] 短信解析失败 storage={storage} index={index} lines={string.Join(" | ", result.Lines)}");
                    return;
                }

                ApplyMessageAnalysis(message);

                if (IsDuplicate(message))
                {
                    _log.Info($"[RealtimeSmsService.ReadSmsAsync] 跳过重复短信 sender={message.Sender} index={index}");
                    return;
                }

                AddMessage(message);
                SetStatusSafe($"收到 {message.Sender} 的新短信");

                if (DeleteAfterRead)
                {
                    // 重新绑定本次动作，避免期间被其他点击覆盖
                    AtActionScope.Current = action;
                    var delete = await _at.SendAsync($"AT+CMGD={index}", 5000).ConfigureAwait(false);
                    if (!delete.Success)
                    {
                        _log.Warn($"[RealtimeSmsService.ReadSmsAsync] 删除模组短信失败 index={index} final={delete.FinalLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[RealtimeSmsService.ReadSmsAsync] 读取/保存短信异常 storage={storage} index={index} error={ex.Message} stack={ex.StackTrace}");
                SetStatusSafe($"接收短信异常：{ex.Message}");
            }
            finally
            {
                _readLock.Release();
            }
        }

        /// <summary>解析 AT+CMGR 结果，同时兼容 PDU 与 TEXT 两种应答。</summary>
        private SmsMessage? ParseCmgr(AtResult result, string storage, int index)
        {
            if (result.Lines.Count == 0)
            {
                return null;
            }

            var headerIndex = result.Lines.FindIndex(l => l.StartsWith("+CMGR:", StringComparison.OrdinalIgnoreCase));
            if (headerIndex < 0)
            {
                headerIndex = 0;
            }

            var header = result.Lines[headerIndex];
            var raw = string.Join(Environment.NewLine, result.Lines);

            if (header.Contains('"'))
            {
                return ParseTextMode(result, headerIndex, storage, index, raw);
            }

            for (var i = headerIndex + 1; i < result.Lines.Count; i++)
            {
                var line = result.Lines[i].Trim();
                if (!IsHexLine(line))
                {
                    continue;
                }

                var decoded = SmsPduDecoder.DecodeDeliver(line);
                if (decoded == null)
                {
                    continue;
                }

                return new SmsMessage
                {
                    Sender = NormalizeSender(decoded.Sender),
                    Body = decoded.Body,
                    ReceivedAt = decoded.Timestamp ?? DateTime.Now,
                    RawText = raw,
                    Storage = storage,
                    StorageIndex = index
                };
            }

            return null;
        }

        /// <summary>解析 TEXT 模式应答。</summary>
        private SmsMessage? ParseTextMode(AtResult result, int headerIndex, string storage, int index, string raw)
        {
            var header = result.Lines[headerIndex];
            var fields = Regex.Matches(header, "\"([^\"]*)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            var sender = fields.Count > 1 ? fields[1] : "未知号码";
            DateTime? timestamp = fields.Count > 3 ? ParseTextTimestamp(fields[3]) : null;

            var body = string.Join(Environment.NewLine, result.Lines.Skip(headerIndex + 1)).Trim();
            if (LooksLikeUcs2Hex(body))
            {
                body = DecodeUcs2Hex(body);
            }

            return new SmsMessage
            {
                Sender = NormalizeSender(sender),
                Body = body,
                ReceivedAt = timestamp ?? DateTime.Now,
                RawText = raw,
                Storage = storage,
                StorageIndex = index
            };
        }

        /// <summary>解析 TEXT 模式时间戳：26/10/24,19:25:00+32。</summary>
        private static DateTime? ParseTextTimestamp(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 17)
            {
                return null;
            }

            return DateTime.TryParseExact(
                value.Substring(0, 17),
                "yy/MM/dd,HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var result)
                ? result
                : (DateTime?)null;
        }

        /// <summary>识别验证码、标签与短信签名服务名。</summary>
        private static void ApplyMessageAnalysis(SmsMessage message)
        {
            var body = message.Body ?? string.Empty;

            var match = Regex.Match(
                body,
                @"(?:验证码|校验码|确认码|动态码|随机密码|密码|口令|code|password)[^0-9]{0,12}(\d{4,8})",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                message.VerificationCode = match.Groups[1].Value;
                message.Tag = Regex.IsMatch(body, @"密码|口令|password", RegexOptions.IgnoreCase) ? "密码" : "验证码";
            }

            var service = Regex.Match(body, @"【([^】]{1,30})】");
            if (service.Success)
            {
                message.ServiceName = service.Groups[1].Value;
            }
        }

        /// <summary>加入消息集合、保存并通知界面。</summary>
        private void AddMessage(SmsMessage message)
        {
            lock (_messagesLock)
            {
                _messages.Add(message);
            }
            SaveAll();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                MessageReceived?.Invoke(message);
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => MessageReceived?.Invoke(message)));
            }
        }

        /// <summary>同一发送方、正文且时间接近的短信视为重复。</summary>
        private bool IsDuplicate(SmsMessage message)
        {
            lock (_messagesLock)
            {
                return _messages.Any(m =>
                    !m.IsOutgoing
                    && m.Sender == message.Sender
                    && m.Body == message.Body
                    && Math.Abs((m.ReceivedAt - message.ReceivedAt).TotalMinutes) < 2);
            }
        }

        /// <summary>启用实时接收：切 PDU 模式并设置 +CNMI=2,1,0,0,0。</summary>
        private async Task EnableRealtimeAsync()
        {
            if (!_serial.IsOpen)
            {
                StatusText = "串口未连接，请先在顶部连接串口";
                IsRealtimeEnabled = false;
                return;
            }

            try
            {
                AtActionScope.Current = new AtAction("启用实时短信");
                StatusText = "正在启用实时接收…";

                var mode = await _at.SendAsync("AT+CMGF=0", 5000);
                var cnmi = await _at.SendAsync("AT+CNMI=2,1,0,0,0", 5000);

                IsRealtimeEnabled = mode.Success && cnmi.Success;
                if (IsRealtimeEnabled)
                {
                    StatusText = "实时接收已开启（PDU 模式）";
                    await QuerySimSlotAsync();
                }
                else
                {
                    StatusText = "启用失败，请检查模组响应";
                    _log.Error($"[RealtimeSmsService.EnableRealtimeAsync] 启用实时接收失败 modeSuccess={mode.Success} cnmiSuccess={cnmi.Success} cmgf={mode.FinalLine} cnmi={cnmi.FinalLine}");
                }
            }
            catch (Exception ex)
            {
                IsRealtimeEnabled = false;
                StatusText = $"启用异常：{ex.Message}";
                _log.Error($"[RealtimeSmsService.EnableRealtimeAsync] 启用实时接收异常 error={ex.Message} stack={ex.StackTrace}");
            }
        }

        /// <summary>查询当前 SIM 卡槽（+ZSWSLOT?）。</summary>
        private async Task QuerySimSlotAsync()
        {
            if (!_serial.IsOpen)
            {
                return;
            }

            try
            {
                var result = await _at.SendAsync("AT+ZSWSLOT?", 3000);
                var text = string.Join("\n", result.Lines);
                var match = Regex.Match(text, @"\+ZSWSLOT:\s*(\d+)");
                if (match.Success)
                {
                    SimSlotText = "SIM " + match.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[RealtimeSmsService.QuerySimSlotAsync] 查询 SIM 卡槽失败 error={ex.Message}");
            }
        }

        /// <summary>从后台线程更新状态文本时调度到 UI 线程。</summary>
        private void SetStatusSafe(string status)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(new Action(() => StatusText = status));
            }
            else
            {
                StatusText = status;
            }
        }

        private static string NormalizeSender(string? sender)
        {
            return string.IsNullOrWhiteSpace(sender) ? "未知号码" : sender.Trim();
        }

        private static bool IsHexLine(string value)
        {
            if (value.Length < 10 || value.Length % 2 != 0)
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (!Uri.IsHexDigit(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool LooksLikeUcs2Hex(string value)
        {
            if (value.Length < 4 || value.Length % 4 != 0)
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (!Uri.IsHexDigit(ch))
                {
                    return false;
                }
            }

            return true;
        }

        private static string DecodeUcs2Hex(string hex)
        {
            try
            {
                var bytes = Convert.FromHexString(hex);
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
            catch (FormatException)
            {
                return hex;
            }
        }

        private static string ToUcs2Hex(string text)
        {
            var sb = new StringBuilder(text.Length * 4);
            foreach (var ch in text)
            {
                sb.Append(((int)ch).ToString("X4"));
            }
            return sb.ToString();
        }
    }
}
