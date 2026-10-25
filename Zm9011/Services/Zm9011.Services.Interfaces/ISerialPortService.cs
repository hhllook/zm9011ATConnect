using System;

namespace Zm9011.Services.Interfaces
{
    /// <summary>串口校验位（与 System.IO.Ports.Parity 对应，接口层自定义避免直接依赖）。</summary>
    public enum ParityMode
    {
        None = 0,
        Odd = 1,
        Even = 2,
        Mark = 3,
        Space = 4
    }

    /// <summary>串口停止位。</summary>
    public enum StopBitMode
    {
        None = 0,
        One = 1,
        Two = 2,
        OnePointFive = 3
    }

    /// <summary>串口流控方式。</summary>
    public enum FlowControlMode
    {
        None = 0,
        XOnXOff = 1,
        RequestToSend = 2,
        RequestToSendXOnXOff = 3
    }

    /// <summary>串口打开参数配置。</summary>
    public class SerialPortConfig
    {
        /// <summary>端口名，如 COM3。</summary>
        public string PortName { get; set; } = string.Empty;

        /// <summary>波特率，默认 115200。</summary>
        public int BaudRate { get; set; } = 115200;

        /// <summary>数据位，默认 8。</summary>
        public int DataBits { get; set; } = 8;

        /// <summary>校验位，默认无校验。</summary>
        public ParityMode Parity { get; set; } = ParityMode.None;

        /// <summary>停止位，默认 1 位。</summary>
        public StopBitMode StopBits { get; set; } = StopBitMode.One;

        /// <summary>流控方式，默认无流控。</summary>
        public FlowControlMode FlowControl { get; set; } = FlowControlMode.None;
    }

    /// <summary>串口数据到达事件参数。</summary>
    public class SerialDataEventArgs : EventArgs
    {
        public SerialDataEventArgs(byte[] data)
        {
            Data = data;
        }

        /// <summary>本次接收到的原始字节。</summary>
        public byte[] Data { get; }
    }

    /// <summary>
    /// 串口服务。封装 System.IO.Ports，负责枚举端口、打开/关闭、收发字节。
    /// </summary>
    public interface ISerialPortService
    {
        /// <summary>串口是否已打开。</summary>
        bool IsOpen { get; }

        /// <summary>当前打开的端口名（未打开时为空）。</summary>
        string CurrentPortName { get; }

        /// <summary>当前波特率（未打开时为 0）。</summary>
        int CurrentBaudRate { get; }

        /// <summary>原始数据到达事件（后台线程触发）。参数包含本次收到的原始字节。</summary>
        event EventHandler<SerialDataEventArgs>? DataReceived;

        /// <summary>串口发生错误（含打开失败、读写异常）事件，参数为异常对象。</summary>
        event EventHandler<Exception>? ErrorOccurred;

        /// <summary>串口已关闭事件（含异常导致的意外关闭）。</summary>
        event EventHandler? Closed;

        /// <summary>枚举本机可用串口名列表。</summary>
        string[] GetPortNames();

        /// <summary>按配置打开串口。失败时抛出异常并触发 <see cref="ErrorOccurred"/>。</summary>
        void Open(SerialPortConfig config);

        /// <summary>关闭串口。未打开时静默返回。</summary>
        void Close();

        /// <summary>发送文本（ASCII 编码写入串口）。</summary>
        void Write(string text);

        /// <summary>发送原始字节（如 Ctrl-Z 0x1A、透传二进制数据）。</summary>
        void Write(byte[] data);
    }
}
