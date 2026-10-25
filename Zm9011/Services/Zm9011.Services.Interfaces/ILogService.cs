using System;

namespace Zm9011.Services.Interfaces
{
    /// <summary>日志级别，用于收发日志区按颜色区分显示。</summary>
    public enum LogLevel
    {
        /// <summary>普通信息（如连接状态变化）。</summary>
        Info,
        /// <summary>发送内容（TE -> 模组）。</summary>
        Tx,
        /// <summary>接收内容（模组 -> TE）。</summary>
        Rx,
        /// <summary>主动上报（URC）。</summary>
        Urc,
        /// <summary>警告。</summary>
        Warn,
        /// <summary>错误。</summary>
        Error
    }

    /// <summary>一条日志记录。</summary>
    public class LogEntry
    {
        /// <summary>日志产生时间。</summary>
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>日志级别。</summary>
        public LogLevel Level { get; set; }

        /// <summary>日志正文。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>所属用户操作 Id（一次按钮点击产生的多条命令共享同一 Id）；为空表示系统信息或主动上报。</summary>
        public string? ActionId { get; set; }

        /// <summary>所属用户操作显示名（按钮功能名）。</summary>
        public string? ActionTitle { get; set; }

        public override string ToString()
        {
            // 前缀标识便于肉眼区分：>> 发送、<< 接收、** 上报、!! 错误
            var prefix = Level switch
            {
                LogLevel.Tx => ">>",
                LogLevel.Rx => "<<",
                LogLevel.Urc => "**",
                LogLevel.Warn => "??",
                LogLevel.Error => "!!",
                _ => "--"
            };
            return $"{Time:HH:mm:ss.fff} {prefix} {Message}";
        }
    }

    /// <summary>
    /// 全局日志服务。所有串口收发、URC、内部错误统一从这里输出，
    /// 由 UI 层订阅 <see cref="EntryLogged"/> 展示到收发日志区。
    /// </summary>
    public interface ILogService
    {
        /// <summary>新日志条目产生事件（可能从后台线程触发，订阅方需自行调度到 UI 线程）。</summary>
        event Action<LogEntry>? EntryLogged;

        /// <summary>写入一条日志。</summary>
        /// <param name="level">日志级别。</param>
        /// <param name="message">日志正文。</param>
        /// <param name="action">所属用户操作上下文；为空时由实现层回退到 <see cref="AtActionScope.Current"/>。</param>
        void Log(LogLevel level, string message, AtAction? action = null);

        /// <summary>写入一条信息级日志。</summary>
        void Info(string message, AtAction? action = null);

        /// <summary>写入一条发送级日志（TE -> 模组）。</summary>
        void Tx(string message, AtAction? action = null);

        /// <summary>写入一条接收级日志（模组 -> TE）。</summary>
        void Rx(string message, AtAction? action = null);

        /// <summary>写入一条 URC 级日志。</summary>
        void Urc(string message, AtAction? action = null);

        /// <summary>写入一条警告级日志（非致命但需关注的问题）。</summary>
        void Warn(string message, AtAction? action = null);

        /// <summary>写入一条错误级日志，同时输出到 Debug 输出窗口。</summary>
        void Error(string message, AtAction? action = null);
    }
}
