using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zm9011.Services.Interfaces;

namespace Zm9011.Services
{
    /// <summary>
    /// AT 指令服务实现。
    /// <para>核心机制：</para>
    /// <list type="bullet">
    /// <item>串行队列：AT 串口为半双工问答式，命令一次只执行一条，其余排队。</item>
    /// <item>响应收集：从原始字节流按 CR/LF 拆行，遇到 OK / ERROR / +CME ERROR / +CMS ERROR / CONNECT 结束。</item>
    /// <item>提示符：AT+CMGS 等命令先回 "&gt; " 提示符等正文，SendAsync 收到提示符即返回，正文由 SendBodyAsync 补发（Ctrl-Z 结束）。</item>
    /// <item>交互式命令：+MYNETWRITE / +MYFTPGET 等两段式命令用 SendInteractiveAsync，先等触发行再发正文。</item>
    /// <item>URC：+CMTI、+MYURCREAD 等主动上报行分发给订阅者。</item>
    /// <item>透传数据：命令等待期之外到达的非 AT 文本原样按 Rx 日志输出。</item>
    /// </list>
    /// </summary>
    public class AtCommandService : IAtCommandService, IDisposable
    {
        /// <summary>URC 前缀集合（明确无请求的主动上报）。注意 +CREG/+CEREG/+CGREG 等既是查询响应也可能是主动上报，
        /// 不放入此表——命令等待期按响应收集，空闲期由 LineReceived 分发。</summary>
        private static readonly string[] UrcPrefixes =
        {
            "+CMTI:", "+CMT:", "+CDS:", "+CDSI:", "+CBM:",
            "+MYURCREAD:", "+MYURCCLOSE:", "+MYURCCLIENT:", "+MYURCFTP:",
            "+ZIND:", "+CRING:", "RING", "+ZURC:"
        };

        private readonly ISerialPortService _serial;
        private readonly ILogService _log;

        /// <summary>待执行命令队列。</summary>
        private readonly ConcurrentQueue<PendingCommand> _queue = new ConcurrentQueue<PendingCommand>();

        /// <summary>队列泵锁。保证同一时刻只有一条命令在写入/等待响应。</summary>
        private readonly SemaphoreSlim _pumpLock = new SemaphoreSlim(1, 1);

        /// <summary>行缓冲：原始字节按行拆分，残行留待下次拼接。</summary>
        private readonly StringBuilder _lineBuffer = new StringBuilder();

        /// <summary>当前正在收集响应的命令（null 表示空闲）。</summary>
        private PendingCommand? _current;

        /// <summary>是否已收到 "&gt; " 提示符但还没发正文。</summary>
        private bool _promptPending;

        private bool _disposed;

        public event Action<string>? UrcReceived;

        public event Action? PromptReceived;

        public event Action<string>? LineReceived;

        public bool IsBusy => _current != null || !_queue.IsEmpty;

        public AtCommandService(ISerialPortService serial, ILogService log)
        {
            _serial = serial;
            _log = log;
            _serial.DataReceived += OnSerialDataReceived;
            _serial.Closed += OnSerialClosed;
        }

        public Task<AtResult> SendAsync(string command, int timeoutMs = 3000)
        {
            // 入队瞬间捕获用户操作上下文，保证本命令的所有收发日志归入同一按钮模块
            var pending = new PendingCommand(command, timeoutMs) { Action = AtActionScope.Current };
            _queue.Enqueue(pending);
            _ = Task.Run(PumpQueueAsync);
            return pending.TaskCompletion.Task;
        }

        public async Task<AtResult> SendBodyAsync(string payload, int timeoutMs = 10000, AtAction? action = null)
        {
            if (!_promptPending)
            {
                var message = "[AtCommandService.SendBodyAsync] 当前没有待正文的提示符，需先执行返回 > 提示符的命令（如 AT+CMGS）";
                _log.Error(message, action);
                return new AtResult { Success = false, ErrorMessage = message };
            }

            _promptPending = false;
            _log.Tx(payload + "<CTRL-Z>", action);
            _serial.Write(payload + "\u001A");

            // 等最终 OK/ERROR：挂一个临时的响应收集上下文
            var tcs = new TaskCompletionSource<AtResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bodyCommand = new PendingCommand("(body)", timeoutMs, tcs) { Action = action };
            _current = bodyCommand;

            try
            {
                var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != tcs.Task)
                {
                    _log.Error($"[AtCommandService.SendBodyAsync] 正文发送后等待响应超时 timeoutMs={timeoutMs} payloadLength={payload?.Length ?? 0}", action);
                    return new AtResult { Success = false, Timeout = true, ErrorMessage = "发送正文后等待响应超时" };
                }
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                if (_current == bodyCommand)
                {
                    _current = null;
                }
                _ = Task.Run(PumpQueueAsync);
            }
        }

        /// <inheritdoc/>
        public async Task<AtResult> SendInteractiveAsync(string command, string triggerPrefix, string? body, int timeoutMs = 15000, bool connectIsFinal = true)
        {
            var pending = new PendingCommand(command, timeoutMs)
            {
                Action = AtActionScope.Current,
                TriggerPrefix = triggerPrefix,
                ConnectIsFinal = connectIsFinal,
                Body = body,
                WaitingTrigger = true
            };
            _queue.Enqueue(pending);
            _ = Task.Run(PumpQueueAsync);

            var result = await pending.TaskCompletion.Task.ConfigureAwait(false);

            // 阶段二：提示符类命令（如 AT+CMGS）收到 ">" 后发正文（Ctrl-Z 结束）再等最终响应
            if (result.Success && triggerPrefix == ">" && result.FinalLine == ">")
            {
                return await SendBodyAsync(body ?? string.Empty, timeoutMs, pending.Action).ConfigureAwait(false);
            }

            return result;
        }

        public void SendRaw(byte[] data)
        {
            try
            {
                _log.Tx($"[原始字节 {data.Length} 字节] " + BitConverter.ToString(data), AtActionScope.Current);
                _serial.Write(data);
            }
            catch (Exception ex)
            {
                _log.Error($"[AtCommandService.SendRaw] 发送原始字节失败 bytes={data.Length} error={ex.Message} stack={ex.StackTrace}", AtActionScope.Current);
            }
        }

        public void SendRaw(string text)
        {
            try
            {
                _log.Tx(text, AtActionScope.Current);
                _serial.Write(text);
            }
            catch (Exception ex)
            {
                _log.Error($"[AtCommandService.SendRaw] 发送原始文本失败 length={text?.Length ?? 0} error={ex.Message} stack={ex.StackTrace}", AtActionScope.Current);
            }
        }

        /// <summary>队列泵：逐条取出命令发送并等待响应完成。多个触发源并发时由信号量串行化。</summary>
        private async Task PumpQueueAsync()
        {
            if (!await _pumpLock.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                while (_queue.TryDequeue(out var cmd))
                {
                    await ExecuteCommandAsync(cmd).ConfigureAwait(false);
                }
            }
            finally
            {
                _pumpLock.Release();
            }
        }

        /// <summary>执行单条命令：写串口、等终止符或超时。</summary>
        private async Task ExecuteCommandAsync(PendingCommand cmd)
        {
            _current = cmd;
            try
            {
                _log.Tx(cmd.Command, cmd.Action);
                _serial.Write(cmd.Command + "\r");
            }
            catch (Exception ex)
            {
                _log.Error($"[AtCommandService.ExecuteCommandAsync] 写串口失败 command={cmd.Command} error={ex.Message} stack={ex.StackTrace}", cmd.Action);
                var fail = cmd.Result;
                fail.Success = false;
                fail.ErrorMessage = $"串口写入失败：{ex.Message}";
                cmd.Complete(fail);
                _current = null;
                return;
            }

            // 等待结束信号（OK/ERROR/提示符/CONNECT）或超时
            var finished = await Task.WhenAny(cmd.TaskCompletion.Task, Task.Delay(cmd.TimeoutMs)).ConfigureAwait(false);
            if (finished != cmd.TaskCompletion.Task)
            {
                _log.Warn($"[AtCommandService.ExecuteCommandAsync] 命令响应超时 command={cmd.Command} timeoutMs={cmd.TimeoutMs} lines={cmd.Result.Lines.Count}", cmd.Action);
                cmd.Result.Timeout = true;
                cmd.Result.ErrorMessage = "响应超时";
                cmd.Complete(cmd.Result);
            }

            _current = null;
        }

        /// <summary>串口字节到达：拆行并逐行分类处理。</summary>
        private void OnSerialDataReceived(object? sender, SerialDataEventArgs e)
        {
            var text = Encoding.ASCII.GetString(e.Data);
            _lineBuffer.Append(text);

            string? line;
            while (TryTakeLine(out line))
            {
                ProcessLine(line);
            }
        }

        /// <summary>从行缓冲取一行（\r、\n 结尾），无完整行返回 false。</summary>
        private bool TryTakeLine(out string line)
        {
            var sb = _lineBuffer;
            var pendingSkip = false;
            for (var i = 0; i < sb.Length; i++)
            {
                var c = sb[i];
                if (c != '\r' && c != '\n')
                {
                    continue;
                }

                line = sb.ToString(0, i).Trim();
                sb.Remove(0, i + 1);
                // 上一次行结尾与本次开头组成 \r\n 或 \n\r 时跳过多余的那个
                if (pendingSkip && sb.Length > 0 && (sb[0] == '\r' || sb[0] == '\n'))
                {
                    sb.Remove(0, 1);
                    pendingSkip = false;
                }
                else
                {
                    pendingSkip = true;
                }
                return true;
            }
            line = string.Empty;
            return false;
        }

        /// <summary>处理一行接收文本：回显丢弃、提示符、触发行、终止符、URC、普通响应行分类。</summary>
        private void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            // 无命令等待且非 AT 语法的行：透传数据，按 Rx 输出
            if (_current == null && !_promptPending && IsTransparentData(line))
            {
                _log.Rx(line);
                return;
            }

            // 回显：等待响应期间出现的、与刚发送命令相同的行
            if (_current != null && _current.Result.RawResponse.Length == 0
                && string.Equals(line, _current.Command, StringComparison.OrdinalIgnoreCase))
            {
                _log.Rx($"[回显] {line}", _current.Action);
                return;
            }

            // "&gt; " 数据输入提示符（模组等待正文，如 AT+CMGS）
            if (line == ">")
            {
                _log.Rx("> (等待正文输入)", _current?.Action);
                _promptPending = true;
                if (_current != null && _current.WaitingTrigger)
                {
                    // 交互式命令（triggerPrefix=">"）：提示符即阶段一完成
                    _current.Result.FinalLine = ">";
                    _current.Result.Success = true;
                    _current.Complete(_current.Result);
                }
                else if (_current == null)
                {
                    // 非交互式 SendAsync 遇到提示符：完成当前等待并通知
                    PromptReceived?.Invoke();
                }
                return;
            }

            // 触发行：交互式命令等待的前缀行（如 "+MYNETWRITE:"、"CONNECT"）
            if (_current != null && _current.WaitingTrigger && MatchesTrigger(_current, line))
            {
                _log.Rx(line, _current.Action);
                _current.WaitingTrigger = false;
                _current.Result.Lines.Add(line);
                _current.Result.RawResponse = line;

                // CONNECT 特殊语义：透传模式（connectIsFinal）下即终止；否则继续等 OK
                if (line == "CONNECT" && _current.ConnectIsFinal)
                {
                    _current.Result.FinalLine = "CONNECT";
                    _current.Result.Success = true;
                    _current.Complete(_current.Result);
                    return;
                }

                // 写入正文（无正文时仅继续等待最终响应，如 MYFTPGET 的 CONNECT）
                if (!string.IsNullOrEmpty(_current.Body))
                {
                    _log.Tx(_current.Body, _current.Action);
                    _serial.Write(_current.Body);
                }
                return;
            }

            // 终止符行
            if (IsFinalLine(line))
            {
                _log.Rx(line, _current?.Action);
                if (_current != null)
                {
                    var r = _current.Result;
                    r.FinalLine = line;
                    r.Success = line == "OK" || line == "CONNECT";
                    if (!r.Success)
                    {
                        r.ErrorMessage = DescribeError(line);
                    }
                    _current.Complete(r);
                }
                return;
            }

            // URC 行：主动上报不属于任何按钮动作，单独成组展示
            if (IsUrc(line))
            {
                _log.Urc(line);
                UrcReceived?.Invoke(line);
                return;
            }

            // 普通响应行
            _log.Rx(line, _current?.Action);
            if (_current != null)
            {
                _current.Result.Lines.Add(line);
                _current.Result.RawResponse = string.IsNullOrEmpty(_current.Result.RawResponse)
                    ? line
                    : _current.Result.RawResponse + Environment.NewLine + line;
            }
            else
            {
                // 无命令等待时的普通行，通过 LineReceived 通知，避免丢失
                LineReceived?.Invoke(line);
            }
        }

        /// <summary>判断行是否命中待执行命令的触发行前缀。</summary>
        private static bool MatchesTrigger(PendingCommand cmd, string line)
        {
            if (cmd.TriggerPrefix == ">")
            {
                return false; // 提示符在 ProcessLine 中单独处理
            }
            return line.StartsWith(cmd.TriggerPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断是否为 URC 行。</summary>
        private static bool IsUrc(string line)
        {
            foreach (var prefix in UrcPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>判断是否为终止响应行。</summary>
        private static bool IsFinalLine(string line)
        {
            return line == "OK"
                || line == "ERROR"
                || line.StartsWith("+CME ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("+CMS ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                || line == "CONNECT";
        }

        /// <summary>把错误码行翻译成可读描述（手册附录 A/B/C 错误表节选）。</summary>
        private static string DescribeError(string line)
        {
            var idx = line.IndexOf(':');
            if (idx < 0)
            {
                return line;
            }
            var codePart = line.Substring(idx + 1).Trim();
            if (!int.TryParse(codePart, out var code))
            {
                // "+CME ERROR: SIM not inserted" 这类文本错误直接返回原文
                return line;
            }

            var desc = code switch
            {
                // 附录 A：扩展指令及 TCP/IP ERROR
                900 => "用户名和密码拨号被网络侧拒绝（APN 错误，SIM 卡欠费等）",
                901 => "PDP 没有激活",
                902 => "此 PDP 已经激活",
                910 => "TCP 连接被对方拒绝",
                911 => "TCP 连接超时，可能 IP 和端口不正确",
                912 => "Socket 连接已经存在",
                913 => "Socket 连接不存在",
                914 => "缓冲区已满，需要重试发送",
                915 => "发送数据超时",
                916 => "域名不存在",
                917 => "域名解析超时",
                918 => "域名解析未知错误",
                980 => "输入参数不合法",
                981 => "其他错误",
                // 附录 B：+CME ERROR（常用项）
                3 => "不允许的操作",
                4 => "不支持的操作",
                10 => "未插 SIM 卡",
                11 => "要求 SIM 卡的 PIN 码",
                12 => "要求 SIM 卡的 PUK 码",
                13 => "SIM 卡失败",
                14 => "SIM 卡忙",
                15 => "SIM 错误",
                16 => "密码错误",
                17 => "请求 SIM 卡的 PIN2 码",
                18 => "请求 SIM 卡的 PUK2 码",
                30 => "无网络服务",
                31 => "网络超时",
                50 => "参数错误",
                100 => "未知错误",
                // 附录 C：+CMS ERROR（常用项）
                310 => "(U)SIM 卡没有插入",
                311 => "(U)SIM 卡的 PIN 请求",
                313 => "(U)SIM 卡失败",
                321 => "无效的内存索引号",
                322 => "内存满",
                330 => "SMSC 地址未知",
                331 => "无网络服务",
                332 => "网络超时",
                340 => "没有期望的+CNMA 确认",
                500 => "未知错误",
                _ => "错误码 " + code
            };
            return $"{line}（{desc}）";
        }

        /// <summary>无命令等待时是否为透传数据（CONNECT 之后模组直接吐出的原始数据行）。</summary>
        private static bool IsTransparentData(string line)
        {
            return !line.StartsWith("AT", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith('+')
                && line != "OK"
                && line != "ERROR"
                && line != "CONNECT";
        }

        /// <summary>串口关闭：失败所有等待中的命令并复位状态。</summary>
        private void OnSerialClosed(object? sender, EventArgs e)
        {
            var pending = _current;
            if (pending != null)
            {
                pending.Result.Success = false;
                pending.Result.ErrorMessage = "串口已关闭";
                pending.Complete(pending.Result);
                _current = null;
            }
            _promptPending = false;
            _lineBuffer.Clear();
        }

        /// <summary>待处理命令上下文。</summary>
        private sealed class PendingCommand
        {
            public PendingCommand(string command, int timeoutMs)
                : this(command, timeoutMs, new TaskCompletionSource<AtResult>(TaskCreationOptions.RunContinuationsAsynchronously))
            {
            }

            public PendingCommand(string command, int timeoutMs, TaskCompletionSource<AtResult> tcs)
            {
                Command = command;
                TimeoutMs = timeoutMs;
                Result = new AtResult();
                TaskCompletion = tcs;
            }

            public string Command { get; }

            public int TimeoutMs { get; }

            /// <summary>命令入队时捕获的用户操作上下文（用于日志按按钮模块归组）。</summary>
            public AtAction? Action { get; set; }

            public AtResult Result { get; }

            public TaskCompletionSource<AtResult> TaskCompletion { get; }

            /// <summary>交互式命令：等待的触发行前缀（"&gt;" 或 "+MYNETWRITE:" 等）。</summary>
            public string TriggerPrefix { get; set; } = string.Empty;

            /// <summary>触发行命中后写入的正文（null/空 表示不发正文）。</summary>
            public string? Body { get; set; }

            /// <summary>是否正在等待触发行。</summary>
            public bool WaitingTrigger { get; set; }

            /// <summary>CONNECT 是否作为终止符（true=透传模式立即返回；false=继续等 OK）。</summary>
            public bool ConnectIsFinal { get; set; } = true;

            public void Complete(AtResult result)
            {
                TaskCompletion.TrySetResult(result);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            OnSerialClosed(this, EventArgs.Empty);
            _serial.DataReceived -= OnSerialDataReceived;
            _serial.Closed -= OnSerialClosed;
        }
    }
}
