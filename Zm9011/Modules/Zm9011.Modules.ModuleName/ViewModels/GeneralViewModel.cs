using System;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// 通用指令页（手册第 2 章）：版本/机型/IMEI/IMSI 查询、PIN 码、软重启、
    /// 恢复出厂、数据/命令模式切换、结果格式、字符集、时钟、功能模式等。
    /// </summary>
    public class GeneralViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public GeneralViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            QueryVersionCommand = new DelegateCommand(async () => await SendAsync("AT+CGMR").ConfigureAwait(false));
            QueryModelCommand = new DelegateCommand(async () => await SendAsync("AT+CGMM").ConfigureAwait(false));
            QueryImsiCommand = new DelegateCommand(async () => await SendAsync("AT+CIMI").ConfigureAwait(false));
            QueryImeiCommand = new DelegateCommand(async () => await SendAsync("AT+CGSN").ConfigureAwait(false));
            QueryManufacturerCommand = new DelegateCommand(async () => await SendAsync("AT+CGMI").ConfigureAwait(false));
            QueryProductInfoCommand = new DelegateCommand(async () => await SendAsync("ATI").ConfigureAwait(false));
            QueryPinCommand = new DelegateCommand(async () => await SendAsync("AT+CPIN?").ConfigureAwait(false));
            QueryUserNumberCommand = new DelegateCommand(async () => await SendAsync("AT+CNUM").ConfigureAwait(false));

            EnterPinCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CPIN={PinCode}").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(PinCode))
                .ObservesProperty(() => PinCode);

            SoftRestartCommand = new DelegateCommand(async () => await SendAsync("AT+ZRTEMP=2").ConfigureAwait(false));
            FactoryResetCommand = new DelegateCommand(async () => await SendAsync("AT+ZRTEMP=3").ConfigureAwait(false));
            SoftResetCommand = new DelegateCommand(async () => await SendAsync($"AT+ZSOFTRESET={SoftResetFlag}").ConfigureAwait(false));
            ZResetCommand = new DelegateCommand(async () => await SendAsync($"AT+ZRESET={ZResetFlag}").ConfigureAwait(false));
            PowerOffCommand = new DelegateCommand(async () => await SendAsync($"AT+QPOWD={PowerOffFlag}").ConfigureAwait(false));

            ExitDataModeCommand = new DelegateCommand(async () =>
            {
                // +++ 需要前后静默期（手册要求 1s），直接写原始串口
                await Task.Delay(1000).ConfigureAwait(false);
                _at.SendRaw("+++");
                await Task.Delay(1000).ConfigureAwait(false);
            });
            EnterDataModeCommand = new DelegateCommand(async () => await SendAsync("ATO").ConfigureAwait(false));

            SaveConfigCommand = new DelegateCommand(async () => await SendAsync("AT&W").ConfigureAwait(false));
            RestoreConfigCommand = new DelegateCommand(async () => await SendAsync("ATZ0").ConfigureAwait(false));
            SetEchoCommand = new DelegateCommand(async () => await SendAsync($"ATE{EchoValue}").ConfigureAwait(false));
            SetResultModeCommand = new DelegateCommand(async () => await SendAsync($"ATQ{ResultMode}").ConfigureAwait(false));
            SetReturnFormatCommand = new DelegateCommand(async () => await SendAsync($"ATV{ReturnFormat}").ConfigureAwait(false));
            SetCmeeCommand = new DelegateCommand(async () => await SendAsync($"AT+CMEE={CmeeValue}").ConfigureAwait(false));
            SetCharsetCommand = new DelegateCommand(async () => await SendAsync($"AT+CSCS=\"{Charset}\"").ConfigureAwait(false));
            QueryCharsetCommand = new DelegateCommand(async () => await SendAsync("AT+CSCS?").ConfigureAwait(false));
            QueryClockCommand = new DelegateCommand(async () =>
            {
                // 手册要求：+CCLK 前先执行 +ZNITZ? 保证 NITZ 时间已同步
                await SendAsync("AT+ZNITZ?").ConfigureAwait(false);
                await SendAsync("AT+CCLK?").ConfigureAwait(false);
            });
            QueryNitzCommand = new DelegateCommand(async () => await SendAsync("AT+ZNITZ?").ConfigureAwait(false));
            QuerySysTimeCommand = new DelegateCommand(async () =>
            {
                await SendAsync("AT+ZNITZ?").ConfigureAwait(false);
                await SendAsync("AT+ZSYSTIME?").ConfigureAwait(false);
            });
            SetFunModeCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CFUN={FunMode},{FunReset}").ConfigureAwait(false));
            QueryFunModeCommand = new DelegateCommand(async () => await SendAsync("AT+CFUN?").ConfigureAwait(false));
            OpenLogCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZOPENLOG={LogKey}").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(LogKey))
                .ObservesProperty(() => LogKey);
        }

        private string _pinCode = string.Empty;
        /// <summary>PIN/PUK 码（4-8 位数字）。</summary>
        public string PinCode { get => _pinCode; set => SetProperty(ref _pinCode, value); }

        private int _softResetFlag = 1;
        /// <summary>+ZSOFTRESET 参数：0 关机 / 1 重启。</summary>
        public int SoftResetFlag { get => _softResetFlag; set => SetProperty(ref _softResetFlag, value); }

        private int _zResetFlag = 0;
        /// <summary>+ZRESET 参数：0 恢复并重启 / 1 恢复不重启。</summary>
        public int ZResetFlag { get => _zResetFlag; set => SetProperty(ref _zResetFlag, value); }

        private int _powerOffFlag = 1;
        /// <summary>+QPOWD 参数：0 直接掉电 / 1 正常关机流程。</summary>
        public int PowerOffFlag { get => _powerOffFlag; set => SetProperty(ref _powerOffFlag, value); }

        private int _echoValue = 0;
        /// <summary>ATE 参数：0 关回显 / 1 开回显。</summary>
        public int EchoValue { get => _echoValue; set => SetProperty(ref _echoValue, value); }

        private int _resultMode = 0;
        /// <summary>ATQ 参数：0 传输结果 / 1 不传输结果。</summary>
        public int ResultMode { get => _resultMode; set => SetProperty(ref _resultMode, value); }

        private int _returnFormat = 1;
        /// <summary>ATV 参数：0 简单格式 / 1 标准格式。</summary>
        public int ReturnFormat { get => _returnFormat; set => SetProperty(ref _returnFormat, value); }

        private int _cmeeValue = 2;
        /// <summary>+CMEE 参数：0 禁用 / 1 数字 / 2 详细文本。</summary>
        public int CmeeValue { get => _cmeeValue; set => SetProperty(ref _cmeeValue, value); }

        private string _charset = "UCS2";
        /// <summary>+CSCS 字符集：GSM / IRA / HEX / UCS2。</summary>
        public string Charset { get => _charset; set => SetProperty(ref _charset, value); }

        private int _funMode = 1;
        /// <summary>+CFUN 参数：0 最小功能 / 1 完全功能 / 4 关闭射频。</summary>
        public int FunMode { get => _funMode; set => SetProperty(ref _funMode, value); }

        private int _funReset = 0;
        /// <summary>+CFUN 的 rst 参数：0 不重启 / 1 重启（仅 fun=1 时有效）。</summary>
        public int FunReset { get => _funReset; set => SetProperty(ref _funReset, value); }

        private string _logKey = string.Empty;
        /// <summary>+ZOPENLOG 秘钥（由 IMEI 计算得出）。</summary>
        public string LogKey { get => _logKey; set => SetProperty(ref _logKey, value); }

        public DelegateCommand QueryVersionCommand { get; }
        public DelegateCommand QueryModelCommand { get; }
        public DelegateCommand QueryImsiCommand { get; }
        public DelegateCommand QueryImeiCommand { get; }
        public DelegateCommand QueryManufacturerCommand { get; }
        public DelegateCommand QueryProductInfoCommand { get; }
        public DelegateCommand QueryPinCommand { get; }
        public DelegateCommand QueryUserNumberCommand { get; }
        public DelegateCommand EnterPinCommand { get; }
        public DelegateCommand SoftRestartCommand { get; }
        public DelegateCommand FactoryResetCommand { get; }
        public DelegateCommand SoftResetCommand { get; }
        public DelegateCommand ZResetCommand { get; }
        public DelegateCommand PowerOffCommand { get; }
        public DelegateCommand ExitDataModeCommand { get; }
        public DelegateCommand EnterDataModeCommand { get; }
        public DelegateCommand SaveConfigCommand { get; }
        public DelegateCommand RestoreConfigCommand { get; }
        public DelegateCommand SetEchoCommand { get; }
        public DelegateCommand SetResultModeCommand { get; }
        public DelegateCommand SetReturnFormatCommand { get; }
        public DelegateCommand SetCmeeCommand { get; }
        public DelegateCommand SetCharsetCommand { get; }
        public DelegateCommand QueryCharsetCommand { get; }
        public DelegateCommand QueryClockCommand { get; }
        public DelegateCommand QueryNitzCommand { get; }
        public DelegateCommand QuerySysTimeCommand { get; }
        public DelegateCommand SetFunModeCommand { get; }
        public DelegateCommand QueryFunModeCommand { get; }
        public DelegateCommand OpenLogCommand { get; }

        /// <summary>统一发送入口：失败时记录错误日志。</summary>
        private async Task SendAsync(string command)
        {
            try
            {
                await _at.SendAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[GeneralViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
