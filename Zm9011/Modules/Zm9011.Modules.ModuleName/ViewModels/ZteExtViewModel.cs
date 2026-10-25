using System;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// ZTE 扩展指令页（手册第 7 章）：注册运营商信息、PIN/PUK 剩余次数、
    /// ICCID、SIM 初始化状态、SIM 热插拔检测、双卡切换、ADB 调试端口。
    /// </summary>
    public class ZteExtViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public ZteExtViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            QueryZdonCommand = new DelegateCommand(async () => await SendAsync("AT+ZDON?").ConfigureAwait(false));
            QueryZpincCommand = new DelegateCommand(async () => await SendAsync("AT+ZPINC?").ConfigureAwait(false));
            QueryZpincFacilityCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZPINC={ZpincFacility}").ConfigureAwait(false));
            QueryCcidCommand = new DelegateCommand(async () => await SendAsync("AT+CCID").ConfigureAwait(false));
            QueryZinistatCommand = new DelegateCommand(async () => await SendAsync("AT+ZINISTAT").ConfigureAwait(false));

            // SIM 热插拔：+SPCARDINFO=5,<P1>,<P2>
            SetSpCardInfoCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+SPCARDINFO=5,{SpCardP1},{SpCardP2}").ConfigureAwait(false));
            QuerySpCardInfoCommand = new DelegateCommand(async () =>
                await SendAsync("AT+SPCARDINFO=5,1,0").ConfigureAwait(false));

            // 双卡切换
            QuerySwSlotCommand = new DelegateCommand(async () => await SendAsync("AT+ZSWSLOT?").ConfigureAwait(false));
            SetSwSlotCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZSWSLOT={SwSlot}").ConfigureAwait(false));

            // ADB 调试端口（需要中兴根据 IMEI 计算的密钥）
            OpenAdbCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ADBPSWD=\"{AdbPassword}\"").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(AdbPassword)).ObservesProperty(() => AdbPassword);
        }

        private string _zpincFacility = "SC";
        /// <summary>+ZPINC facility：SC（PIN1）/ P2（PIN2）。</summary>
        public string ZpincFacility { get => _zpincFacility; set => SetProperty(ref _zpincFacility, value); }

        private int _spCardP1 = 0;
        /// <summary>+SPCARDINFO P1：0 设置模式 / 1 查询模式。</summary>
        public int SpCardP1 { get => _spCardP1; set => SetProperty(ref _spCardP1, value); }

        private int _spCardP2 = 1;
        /// <summary>+SPCARDINFO P2：0 关热插拔 / 1 开热插拔 / 3 双卡低电平 / 771 双卡高电平。</summary>
        public int SpCardP2 { get => _spCardP2; set => SetProperty(ref _spCardP2, value); }

        private int _swSlot = 1;
        /// <summary>+ZSWSLOT 卡槽号：1 / 2。</summary>
        public int SwSlot { get => _swSlot; set => SetProperty(ref _swSlot, value); }

        private string _adbPassword = string.Empty;
        /// <summary>ADB 开启密钥（由 IMEI 计算得出）。</summary>
        public string AdbPassword { get => _adbPassword; set => SetProperty(ref _adbPassword, value); }

        public DelegateCommand QueryZdonCommand { get; }
        public DelegateCommand QueryZpincCommand { get; }
        public DelegateCommand QueryZpincFacilityCommand { get; }
        public DelegateCommand QueryCcidCommand { get; }
        public DelegateCommand QueryZinistatCommand { get; }
        public DelegateCommand SetSpCardInfoCommand { get; }
        public DelegateCommand QuerySpCardInfoCommand { get; }
        public DelegateCommand QuerySwSlotCommand { get; }
        public DelegateCommand SetSwSlotCommand { get; }
        public DelegateCommand OpenAdbCommand { get; }

        public Array ZpincFacilities => new[] { "SC", "P2" };

        /// <summary>统一发送入口：失败时记录错误日志。</summary>
        private async Task SendAsync(string command)
        {
            try
            {
                await _at.SendAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[ZteExtViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
