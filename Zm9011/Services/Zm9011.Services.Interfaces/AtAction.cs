using System;

namespace Zm9011.Services.Interfaces
{
    /// <summary>
    /// 一次用户操作的上下文（通常对应一次按钮点击）。
    /// AT 服务在命令入队时读取该上下文，并把 Id / Title 附加到本次命令产生的
    /// 发送、接收、系统日志上，界面据此把日志聚合成“按钮模块”。
    /// </summary>
    public sealed class AtAction
    {
        public AtAction(string title)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "AT 指令" : title;
        }

        /// <summary>操作唯一标识（同一次点击产生的多条命令共享同一 Id）。</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        /// <summary>操作显示名（按钮上的功能名）。</summary>
        public string Title { get; }
    }

    /// <summary>
    /// 当前用户操作上下文。UI 在按钮命令执行前设置，AT 服务在命令入队时读取。
    /// 串口命令为半双工串行执行，因此使用一个进程级静态上下文即可满足归组需求。
    /// </summary>
    public static class AtActionScope
    {
        /// <summary>当前操作；null 表示非按钮触发的后台动作。</summary>
        public static AtAction? Current { get; set; }
    }
}
