using System;
using System.Collections.ObjectModel;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.ViewModels
{
    /// <summary>日志模块类型。</summary>
    public enum LogSessionKind
    {
        /// <summary>一次按钮点击产生的指令动作。</summary>
        Action,
        /// <summary>模组主动上报（URC）。</summary>
        Urc,
        /// <summary>系统 / 内部日志。</summary>
        System,
        /// <summary>透传模式下的原始数据。</summary>
        Data
    }

    /// <summary>模块内的一行日志，带方向前缀便于扫读。</summary>
    public class LogLine
    {
        public LogLine(LogLevel level, string text)
        {
            Level = level;
            Text = text;
        }

        public LogLevel Level { get; }

        public string Text { get; }

        /// <summary>显示文本：→ 发送、← 接收、• 上报/信息、! 警告、✕ 错误。</summary>
        public string DisplayText => Level switch
        {
            LogLevel.Tx => "→ " + Text,
            LogLevel.Rx => "← " + Text,
            LogLevel.Urc => "• " + Text,
            LogLevel.Warn => "! " + Text,
            LogLevel.Error => "✕ " + Text,
            _ => "• " + Text
        };
    }

    /// <summary>
    /// 一个日志模块：对应一次按钮点击（或一组主动上报 / 系统信息）。
    /// 模块首行是按钮功能名，下面依次是该动作执行的命令与模组回复。
    /// </summary>
    public class LogSession : BindableBase
    {
        /// <summary>单个模块最多保留的行数，防止 FOTA 分包等长动作拖慢界面。</summary>
        private const int MaxLines = 400;

        private string _status;
        private int _omittedLines;

        public LogSession(string id, string title, LogSessionKind kind, DateTime startedAt)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? "AT 指令" : title;
            Kind = kind;
            StartedAt = startedAt;

            // 只有指令动作显示状态标签；上报/系统模块无需状态
            _status = kind == LogSessionKind.Action ? "进行中" : string.Empty;
        }

        /// <summary>动作 Id（同一次点击的多条命令共享）。</summary>
        public string Id { get; }

        /// <summary>模块标题（按钮上的功能名）。</summary>
        public string Title { get; }

        public LogSessionKind Kind { get; }

        public DateTime StartedAt { get; }

        /// <summary>模块创建时间（HH:mm:ss）。</summary>
        public string TimeText => StartedAt.ToString("HH:mm:ss");

        /// <summary>模块内的日志行。</summary>
        public ObservableCollection<LogLine> Lines { get; } = new();

        /// <summary>执行状态：进行中 / 成功 / 失败 / 超时；上报与系统模块为空。</summary>
        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        /// <summary>是否显示状态标签。</summary>
        public bool HasStatus => !string.IsNullOrEmpty(Status);

        /// <summary>是否已因行数超限而截断。</summary>
        public bool IsTruncated => _omittedLines > 0;

        /// <summary>截断提示文本。</summary>
        public string TruncatedText => $"… 已省略 {_omittedLines} 行";

        /// <summary>最近一次写入时间，用于 URC / 系统模块的合并判断。</summary>
        public DateTime LastActivity { get; private set; }

        /// <summary>追加一行日志。</summary>
        public void Add(LogLevel level, string message)
        {
            LastActivity = DateTime.Now;

            if (Lines.Count >= MaxLines)
            {
                _omittedLines++;
                RaisePropertyChanged(nameof(IsTruncated));
                RaisePropertyChanged(nameof(TruncatedText));
                return;
            }

            Lines.Add(new LogLine(level, message));

            // 动作模块出现新的发送行，说明还在执行中
            if (Kind == LogSessionKind.Action && level == LogLevel.Tx)
            {
                Status = "进行中";
            }
        }

        /// <summary>更新执行状态。</summary>
        public void SetStatus(string status)
        {
            if (Kind != LogSessionKind.Action)
            {
                return;
            }

            if (Status != status)
            {
                Status = status;
                RaisePropertyChanged(nameof(HasStatus));
            }
        }
    }
}
