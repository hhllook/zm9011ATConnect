using System;
using System.IO.Ports;
using Zm9011.Services.Interfaces;

namespace Zm9011.Services
{
    /// <summary>
    /// 串口服务实现。封装 System.IO.Ports.SerialPort，
    /// 数据到达后在独立线程读取并分发，读取/事件回调中的异常统一记录后通过事件抛给上层。
    /// </summary>
    public class SerialPortService : ISerialPortService
    {
        private readonly ILogService _log;

        private SerialPort? _port;

        public bool IsOpen => _port?.IsOpen == true;

        public string CurrentPortName => _port?.PortName ?? string.Empty;

        public int CurrentBaudRate => _port?.BaudRate ?? 0;

        public event EventHandler<SerialDataEventArgs>? DataReceived;

        public event EventHandler<Exception>? ErrorOccurred;

        public event EventHandler? Closed;

        public SerialPortService(ILogService log)
        {
            _log = log;
        }

        public string[] GetPortNames()
        {
            try
            {
                return SerialPort.GetPortNames();
            }
            catch (Exception ex)
            {
                // 枚举端口失败（罕见，驱动异常等），记录后返回空数组
                _log.Error($"[SerialPortService.GetPortNames] 枚举串口失败 error={ex.Message} stack={ex.StackTrace}");
                ErrorOccurred?.Invoke(this, ex);
                return Array.Empty<string>();
            }
        }

        public void Open(SerialPortConfig config)
        {
            if (IsOpen)
            {
                throw new InvalidOperationException($"串口已打开：{CurrentPortName}，请先关闭后再打开新端口");
            }

            try
            {
                var port = new SerialPort(config.PortName, config.BaudRate, (Parity)config.Parity, config.DataBits, (StopBits)config.StopBits)
                {
                    // AT 指令手册要求行结束为 CR；不开 RTS/CTS 流控，由上层按需控制
                    Handshake = (Handshake)config.FlowControl,
                    ReadTimeout = 200,
                    WriteTimeout = 2000,
                    NewLine = "\r",
                    DtrEnable = true,
                    RtsEnable = true
                };
                port.DataReceived += OnPortDataReceived;
                port.ErrorReceived += OnPortErrorReceived;
                port.Open();

                _port = port;
                _log.Info($"[SerialPortService.Open] 串口已打开 port={config.PortName} baud={config.BaudRate} parity={config.Parity} dataBits={config.DataBits} stopBits={config.StopBits} flowControl={config.FlowControl}");
            }
            catch (Exception ex)
            {
                // 打开失败（被占用/不存在/权限不足），记录关键参数与堆栈
                _log.Error($"[SerialPortService.Open] 打开串口失败 port={config.PortName} baud={config.BaudRate} error={ex.Message} stack={ex.StackTrace}");
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        public void Close()
        {
            var port = _port;
            if (port == null || !port.IsOpen)
            {
                return;
            }

            try
            {
                port.DataReceived -= OnPortDataReceived;
                port.ErrorReceived -= OnPortErrorReceived;
                port.Close();
                _log.Info($"[SerialPortService.Close] 串口已关闭 port={port.PortName}");
            }
            catch (Exception ex)
            {
                // 关闭失败通常发生在设备已拔出时，记录后继续清理
                _log.Error($"[SerialPortService.Close] 关闭串口异常 port={port.PortName} error={ex.Message} stack={ex.StackTrace}");
            }
            finally
            {
                _port = null;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Write(string text)
        {
            var port = _port;
            if (port == null || !port.IsOpen)
            {
                throw new InvalidOperationException("串口未打开，无法发送文本");
            }

            try
            {
                port.Write(text);
            }
            catch (Exception ex)
            {
                _log.Error($"[SerialPortService.Write] 发送文本失败 port={port.PortName} textLength={text?.Length ?? 0} error={ex.Message} stack={ex.StackTrace}");
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        public void Write(byte[] data)
        {
            var port = _port;
            if (port == null || !port.IsOpen)
            {
                throw new InvalidOperationException("串口未打开，无法发送字节");
            }

            try
            {
                port.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                _log.Error($"[SerialPortService.Write] 发送字节失败 port={port.PortName} bytes={data?.Length ?? 0} error={ex.Message} stack={ex.StackTrace}");
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>串口数据到达回调（SerialPort 内部线程）。只负责读取原始字节并转发，解析在 AtCommandService 中进行。</summary>
        private void OnPortDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var port = _port;
            if (port == null)
            {
                return;
            }

            try
            {
                // 循环读到暂无数据，避免大块数据分多次事件才收完
                var buffer = new byte[4096];
                using var ms = new System.IO.MemoryStream();
                while (port.BytesToRead > 0)
                {
                    var n = port.Read(buffer, 0, buffer.Length);
                    if (n <= 0)
                    {
                        break;
                    }
                    ms.Write(buffer, 0, n);
                }

                if (ms.Length > 0)
                {
                    DataReceived?.Invoke(this, new SerialDataEventArgs(ms.ToArray()));
                }
            }
            catch (Exception ex)
            {
                // 读取异常常见于设备拔出瞬间，记录并通知上层做关闭处理
                _log.Error($"[SerialPortService.OnPortDataReceived] 读取串口数据失败 port={port.PortName} error={ex.Message} stack={ex.StackTrace}");
                ErrorOccurred?.Invoke(this, ex);
            }
        }

        private void OnPortErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            // 过载/帧错误等硬件层错误，仅记录，不中断会话
            _log.Warn($"[SerialPortService.OnPortErrorReceived] 串口硬件错误 eventType={e.EventType} port={_port?.PortName}");
        }
    }
}
