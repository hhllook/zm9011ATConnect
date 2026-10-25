using Prism.Events;

namespace Zm9011.Core.Events
{
    /// <summary>
    /// 串口连接成功事件。主窗口在 AT 探测成功后发布，
    /// 实时短信服务收到后自动开启 +CNMI 主动上报。
    /// </summary>
    public class SerialConnectedEvent : PubSubEvent
    {
    }
}
