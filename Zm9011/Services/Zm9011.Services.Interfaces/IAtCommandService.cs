using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Zm9011.Services.Interfaces
{
    /// <summary>AT 命令执行结果。</summary>
    public class AtResult
    {
        /// <summary>是否成功（以 OK 结尾视为成功）。</summary>
        public bool Success { get; set; }

        /// <summary>终止符：OK / ERROR / +CME ERROR: xx / +CMS ERROR: xx 等。</summary>
        public string FinalLine { get; set; } = string.Empty;

        /// <summary>OK 之前的全部中间响应行（不含 URC）。</summary>
        public List<string> Lines { get; set; } = new List<string>();

        /// <summary>执行期间收到的 URC 行（如 +CMTI、+MYURCREAD）。</summary>
        public List<string> UrcLines { get; set; } = new List<string>();

        /// <summary>全部原始文本（含回显）。</summary>
        public string RawResponse { get; set; } = string.Empty;

        /// <summary>是否超时未收到响应。</summary>
        public bool Timeout { get; set; }

        /// <summary>错误描述（Success 为 false 时给出人类可读原因）。</summary>
        public string ErrorMessage { get; set; } = string.Empty;

        public override string ToString()
        {
            return Success
                ? string.Join(Environment.NewLine, Lines)
                : (Timeout ? "[超时无响应] " : "") + FinalLine;
        }
    }

    /// <summary>
    /// AT 指令服务。负责把命令排队、写入串口、收集响应直到终止符（OK/ERROR/>提示符），
    /// 并识别 URC 行分发给订阅者。半双工串行执行，同一时刻只有一条命令在等待响应。
    /// </summary>
    public interface IAtCommandService
    {
        /// <summary>收到 URC（主动上报）行事件，参数为该行内容，如 "+CMTI: "SM",3"。</summary>
        event Action<string>? UrcReceived;

        /// <summary>模组侧进入 "> " 数据输入提示符（等待正文）事件。</summary>
        event Action? PromptReceived;

        /// <summary>收到一行非 URC 的普通响应行事件。</summary>
        event Action<string>? LineReceived;

        /// <summary>当前是否有命令正在执行（串行队列忙）。</summary>
        bool IsBusy { get; }

        /// <summary>执行一条 AT 命令并等待响应完成（OK/ERROR/超时）。串行排队执行。</summary>
        /// <param name="command">命令内容，如 "AT+CGMM"，调用方不带行尾 CR。</param>
        /// <param name="timeoutMs">超时毫秒数，默认 3000。返回 > 提示符的命令（如 CMGS）会先等提示符，等待时间同超时。</param>
        Task<AtResult> SendAsync(string command, int timeoutMs = 3000);

        /// <summary>
        /// 在收到 "&gt; " 提示符后发送数据正文并以 Ctrl+Z (0x1A) 结束（短信/PDU/FOTA 场景）。
        /// 必须在 SendAsync 返回的 AtResult 表示"已收到提示符"后调用。
        /// </summary>
        /// <param name="payload">正文文本（TEXT 模式内容或 HEX 串），以 ASCII 写入。</param>
        /// <param name="timeoutMs">等待最终 OK/ERROR 的超时毫秒数。</param>
        /// <param name="action">所属用户操作上下文；不传时沿用命令入队时捕获的上下文。</param>
        Task<AtResult> SendBodyAsync(string payload, int timeoutMs = 10000, AtAction? action = null);

        /// <summary>
        /// 交互式执行：发送命令后等待触发行（如 "+MYNETWRITE:"、"CONNECT"、"#"提示符），
        /// 触发后写入正文（可为空），再等待最终 OK/ERROR/超时。
        /// 适用于 +CMGS（&gt; 提示符）、+MYNETWRITE、+MYFTPGET（CONNECT 后跟数据）等两段式命令。
        /// </summary>
        /// <param name="command">AT 命令，不带行尾 CR。</param>
        /// <param name="triggerPrefix">触发行前缀："&gt;" 表示输入提示符；如 "+MYNETWRITE:"、"CONNECT"。</param>
        /// <param name="body">触发后写入的正文；null 表示不写正文（如 FTPGET 只等数据）。</param>
        /// <param name="timeoutMs">总超时毫秒数（含等待触发与最终响应）。</param>
        /// <param name="connectIsFinal">true 时 CONNECT 作为终止符立即返回（透传 MYNETCREATE）；
        /// false 时 CONNECT 仅作为触发行，继续等待 OK（MYFTPGET/MYFTPPUT）。</param>
        Task<AtResult> SendInteractiveAsync(string command, string triggerPrefix, string? body, int timeoutMs = 15000, bool connectIsFinal = true);

        /// <summary>发送原始字节到串口（透传模式数据、测试用途），不等待响应。</summary>
        void SendRaw(byte[] data);

        /// <summary>发送原始文本到串口（透传模式数据），不等待响应。</summary>
        void SendRaw(string text);
    }
}
