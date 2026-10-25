using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// UART 串口页（手册第 9、10 章）：串口参数设置（+UARTPARA）、
    /// 波特率自适应开关（+UARTSELFADAPTION）、波特率探测（当前配置下发 AT 试探）。
    /// </summary>
    public class UartViewModel : BindableBase
    {
        /// <summary>+UARTPARA 支持的波特率列表（手册 9.1.3）。</summary>
        public static readonly int[] SupportedBaudRates =
            { 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 2000000, 3000000 };

        private readonly IAtCommandService _at;
        private readonly ISerialPortService _serial;
        private readonly ILogService _log;

        public UartViewModel(IAtCommandService at, ISerialPortService serial, ILogService log)
        {
            _at = at;
            _serial = serial;
            _log = log;

            // 设置串口参数
            SetUartParaCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+UARTPARA={BaudRate},{Parity},{DataBits},{StopBits},{FlowControl}", 5000).ConfigureAwait(false));
            QueryUartParaCommand = new DelegateCommand(async () => await SendAsync("AT+UARTPARA?").ConfigureAwait(false));
            TestUartParaCommand = new DelegateCommand(async () => await SendAsync("AT+UARTPARA=?").ConfigureAwait(false));

            // 波特率自适应开关
            QueryAdaptionCommand = new DelegateCommand(async () => await SendAsync("AT+UARTSELFADAPTION?").ConfigureAwait(false));
            SetAdaptionCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+UARTSELFADAPTION={AdaptionFlag}").ConfigureAwait(false));

            // 手动探测波特率：按当前串口配置发 AT 试探
            ProbeCommand = new DelegateCommand(async () => await ProbeCurrentAsync().ConfigureAwait(false));
        }

        private int _baudRate = 115200;
        /// <summary>波特率（+UARTPARA 第 1 参数）。</summary>
        public int BaudRate { get => _baudRate; set => SetProperty(ref _baudRate, value); }

        private int _parity;
        /// <summary>校验位（ZM9011 固定 0）。</summary>
        public int Parity { get => _parity; set => SetProperty(ref _parity, value); }

        private int _dataBits = 8;
        /// <summary>数据位（ZM9011 固定 8）。</summary>
        public int DataBits { get => _dataBits; set => SetProperty(ref _dataBits, value); }

        private int _stopBits = 1;
        /// <summary>停止位（ZM9011 固定 1）。</summary>
        public int StopBits { get => _stopBits; set => SetProperty(ref _stopBits, value); }

        private int _flowControl;
        /// <summary>流控（ZM9011 固定 0）。</summary>
        public int FlowControl { get => _flowControl; set => SetProperty(ref _flowControl, value); }

        private int _adaptionFlag = 1;
        /// <summary>波特率自适应开关：0 关 / 1 开（默认开启）。</summary>
        public int AdaptionFlag { get => _adaptionFlag; set => SetProperty(ref _adaptionFlag, value); }

        private bool _isProbing;
        /// <summary>是否正在探测波特率。</summary>
        public bool IsProbing { get => _isProbing; set => SetProperty(ref _isProbing, value); }

        private string _probeResult = string.Empty;
        /// <summary>探测结果描述。</summary>
        public string ProbeResult { get => _probeResult; set => SetProperty(ref _probeResult, value); }

        public DelegateCommand SetUartParaCommand { get; }
        public DelegateCommand QueryUartParaCommand { get; }
        public DelegateCommand TestUartParaCommand { get; }
        public DelegateCommand QueryAdaptionCommand { get; }
        public DelegateCommand SetAdaptionCommand { get; }
        public DelegateCommand ProbeCommand { get; }

        public int[] BaudRateOptions => SupportedBaudRates;

        /// <summary>
        /// 手动探测：对当前打开的串口发 AT，超时则提示用户换波特率重试。
        /// 完整自适应探测（遍历波特率重开串口）由主窗口的串口服务完成，见 MainWindowViewModel.AutoDetectBaudRateAsync。
        /// </summary>
        private async Task ProbeCurrentAsync()
        {
            if (IsProbing)
            {
                return;
            }
            if (!_serial.IsOpen)
            {
                ProbeResult = "串口未打开";
                return;
            }

            try
            {
                IsProbing = true;
                ProbeResult = "探测中...";
                var result = await _at.SendAsync("AT", 1000).ConfigureAwait(false);
                ProbeResult = result.Success
                    ? $"当前波特率 {_serial.CurrentBaudRate} 通信正常"
                    : "无响应，请尝试其他波特率";
            }
            catch (Exception ex)
            {
                _log.Error($"[UartViewModel.ProbeCurrentAsync] 探测失败 error={ex.Message} stack={ex.StackTrace}");
                ProbeResult = $"探测失败：{ex.Message}";
            }
            finally
            {
                IsProbing = false;
            }
        }

        /// <summary>统一发送入口：失败时记录错误日志。</summary>
        private async Task SendAsync(string command, int timeoutMs = 3000)
        {
            try
            {
                await _at.SendAsync(command, timeoutMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[UartViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
