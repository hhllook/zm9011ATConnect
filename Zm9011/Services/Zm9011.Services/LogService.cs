using System;
using System.Diagnostics;
using Zm9011.Services.Interfaces;

namespace Zm9011.Services
{
    /// <summary>
    /// 全局日志服务实现。日志同时输出到 Debug 跟踪器，便于开发期排查；
    /// UI 层订阅 EntryLogged 展示到收发日志区。
    /// </summary>
    public class LogService : ILogService
    {
        /// <summary>后台错误日志专用锁，避免多线程写 Debug 输出交错。</summary>
        private readonly object _sync = new object();

        public event Action<LogEntry>? EntryLogged;

        public void Log(LogLevel level, string message, AtAction? action = null)
        {
            // 未显式指定操作上下文时，回退到当前按钮动作，便于把执行过程中的内部日志归入同一模块
            action ??= AtActionScope.Current;
            var entry = new LogEntry
            {
                Level = level,
                Message = message,
                ActionId = action?.Id,
                ActionTitle = action?.Title
            };
            lock (_sync)
            {
                // Debug 输出便于开发期定位；同时通知 UI 订阅者
                Debug.WriteLine($"[{entry.Time:HH:mm:ss.fff}][{level}] {message}");
                EntryLogged?.Invoke(entry);
            }
        }

        public void Info(string message, AtAction? action = null) => Log(LogLevel.Info, message, action);

        public void Tx(string message, AtAction? action = null) => Log(LogLevel.Tx, message, action);

        public void Rx(string message, AtAction? action = null) => Log(LogLevel.Rx, message, action);

        public void Urc(string message, AtAction? action = null) => Log(LogLevel.Urc, message, action);

        public void Warn(string message, AtAction? action = null) => Log(LogLevel.Warn, message, action);

        public void Error(string message, AtAction? action = null)
        {
            // 错误日志必须能定位：附带调用点堆栈信息输出到 Debug 跟踪器
            lock (_sync)
            {
                var stack = new StackTrace(skipFrames: 1, fNeedFileInfo: false);
                Debug.WriteLine($"[ERROR] {message}{Environment.NewLine}  at {stack}");
            }
            Log(LogLevel.Error, message, action);
        }
    }
}
