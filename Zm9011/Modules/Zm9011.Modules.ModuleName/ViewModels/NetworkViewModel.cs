using System;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// 网络服务页（手册第 3 章）：网络注册状态查询（CREG/CEREG/CGREG）、运营商、
    /// 附着、PLMN 列表、时间同步、选网模式、小区信息、信号、锁频段/锁小区。
    /// </summary>
    public class NetworkViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public NetworkViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            QueryCregCommand = new DelegateCommand(async () =>
            {
                await SendAsync($"AT+CREG={CregMode}").ConfigureAwait(false);
                await SendAsync("AT+CREG?").ConfigureAwait(false);
            });
            QueryCeregCommand = new DelegateCommand(async () =>
            {
                await SendAsync($"AT+CEREG={CeregMode}").ConfigureAwait(false);
                await SendAsync("AT+CEREG?").ConfigureAwait(false);
            });
            QueryCgregCommand = new DelegateCommand(async () =>
            {
                await SendAsync($"AT+CGREG={CgregMode}").ConfigureAwait(false);
                await SendAsync("AT+CGREG?").ConfigureAwait(false);
            });
            QueryCopsCommand = new DelegateCommand(async () => await SendAsync("AT+COPS?").ConfigureAwait(false));
            QueryCgattCommand = new DelegateCommand(async () => await SendAsync("AT+CGATT?").ConfigureAwait(false));
            SetCgattCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CGATT={CgattState}").ConfigureAwait(false));
            QueryCpolCommand = new DelegateCommand(async () => await SendAsync("AT+CPOL?").ConfigureAwait(false));

            QueryZnitzCommand = new DelegateCommand(async () => await SendAsync("AT+ZNITZ?").ConfigureAwait(false));
            QueryZpasCommand = new DelegateCommand(async () => await SendAsync("AT+ZPAS?").ConfigureAwait(false));
            SetZsntCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZSNT={ZsntMode},0,0").ConfigureAwait(false));
            QueryZsntCommand = new DelegateCommand(async () => await SendAsync("AT+ZSNT?").ConfigureAwait(false));
            QueryCellInfoCommand = new DelegateCommand(async () => await SendAsync("AT+ZCELLINFO?").ConfigureAwait(false));
            QueryZrssiCommand = new DelegateCommand(async () => await SendAsync("AT+ZRSSI?").ConfigureAwait(false));
            QuerySp5gCommand = new DelegateCommand(async () =>
                await SendAsync("AT+SP5GCMDS=\"get nr support_band\"", 5000).ConfigureAwait(false));

            // 锁小区
            Lock4gCellCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZLOCKCELL={Lock4gEarfcn},{Lock4gPci}").ConfigureAwait(false),
                () => Lock4gEarfcn > 0 && Lock4gPci >= 0)
                .ObservesProperty(() => Lock4gEarfcn).ObservesProperty(() => Lock4gPci);
            QueryLock4gCellCommand = new DelegateCommand(async () => await SendAsync("AT+ZLOCKCELL?").ConfigureAwait(false));
            Lock5gCellCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZLOCKNRCELL={Lock5gPci},{Lock5gArfcn}").ConfigureAwait(false),
                () => Lock5gPci >= 0 && Lock5gArfcn > 0)
                .ObservesProperty(() => Lock5gPci).ObservesProperty(() => Lock5gArfcn);
            QueryLock5gCellCommand = new DelegateCommand(async () => await SendAsync("AT+ZLOCKNRCELL?").ConfigureAwait(false));

            // 锁频段
            Lock4gBandCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZNLOCKBAND={LteBandEnable},0,{LteBandMask},0").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(LteBandMask))
                .ObservesProperty(() => LteBandMask);
            QueryLock4gBandCommand = new DelegateCommand(async () => await SendAsync("AT+ZNLOCKBAND?").ConfigureAwait(false));
            Lock5gBandCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZNLOCKNRBAND=\"{NrBandList}\"").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(NrBandList))
                .ObservesProperty(() => NrBandList);
            QueryLock5gBandCommand = new DelegateCommand(async () => await SendAsync("AT+ZNLOCKNRBAND?").ConfigureAwait(false));
        }

        private int _cregMode = 2;
        /// <summary>+CREG 的 n 参数：0 不上报 / 1 上报状态 / 2 上报状态+位置信息。</summary>
        public int CregMode { get => _cregMode; set => SetProperty(ref _cregMode, value); }

        private int _ceregMode = 2;
        /// <summary>+CEREG 的 n 参数。</summary>
        public int CeregMode { get => _ceregMode; set => SetProperty(ref _ceregMode, value); }

        private int _cgregMode = 2;
        /// <summary>+CGREG 的 n 参数。</summary>
        public int CgregMode { get => _cgregMode; set => SetProperty(ref _cgregMode, value); }

        private int _cgattState = 1;
        /// <summary>+CGATT 参数：0 去附着 / 1 附着。</summary>
        public int CgattState { get => _cgattState; set => SetProperty(ref _cgattState, value); }

        private int _zsntMode = 16;
        /// <summary>+ZSNT 选网模式：1=2G 2=3G 3=2G+3G 6=4G 7=3G+4G 16=自动 17=4G+5G 18=SA only。</summary>
        public int ZsntMode { get => _zsntMode; set => SetProperty(ref _zsntMode, value); }

        private int _lock4gEarfcn;
        /// <summary>锁 4G 小区的 EARFCN 频点。</summary>
        public int Lock4gEarfcn { get => _lock4gEarfcn; set => SetProperty(ref _lock4gEarfcn, value); }

        private int _lock4gPci;
        /// <summary>锁 4G 小区的 PCI。</summary>
        public int Lock4gPci { get => _lock4gPci; set => SetProperty(ref _lock4gPci, value); }

        private int _lock5gPci;
        /// <summary>锁 5G 小区的 PCI。</summary>
        public int Lock5gPci { get => _lock5gPci; set => SetProperty(ref _lock5gPci, value); }

        private int _lock5gArfcn;
        /// <summary>锁 5G 小区的 NR-ARFCN 频点。</summary>
        public int Lock5gArfcn { get => _lock5gArfcn; set => SetProperty(ref _lock5gArfcn, value); }

        private string _lteBandMask = string.Empty;
        /// <summary>锁 4G 频段的 16 进制掩码（不带 0x），如 1c2000000d7。</summary>
        public string LteBandMask { get => _lteBandMask; set => SetProperty(ref _lteBandMask, value); }

        private int _lteBandEnable = 1;
        /// <summary>锁 LTE 频段使能：0 不锁定 / 1 锁定。</summary>
        public int LteBandEnable { get => _lteBandEnable; set => SetProperty(ref _lteBandEnable, value); }

        private string _nrBandList = string.Empty;
        /// <summary>锁 5G 频段列表，逗号分隔，如 1,78。</summary>
        public string NrBandList { get => _nrBandList; set => SetProperty(ref _nrBandList, value); }

        public DelegateCommand QueryCregCommand { get; }
        public DelegateCommand QueryCeregCommand { get; }
        public DelegateCommand QueryCgregCommand { get; }
        public DelegateCommand QueryCopsCommand { get; }
        public DelegateCommand QueryCgattCommand { get; }
        public DelegateCommand SetCgattCommand { get; }
        public DelegateCommand QueryCpolCommand { get; }
        public DelegateCommand QueryZnitzCommand { get; }
        public DelegateCommand QueryZpasCommand { get; }
        public DelegateCommand SetZsntCommand { get; }
        public DelegateCommand QueryZsntCommand { get; }
        public DelegateCommand QueryCellInfoCommand { get; }
        public DelegateCommand QueryZrssiCommand { get; }
        public DelegateCommand QuerySp5gCommand { get; }
        public DelegateCommand Lock4gCellCommand { get; }
        public DelegateCommand QueryLock4gCellCommand { get; }
        public DelegateCommand Lock5gCellCommand { get; }
        public DelegateCommand QueryLock5gCellCommand { get; }
        public DelegateCommand Lock4gBandCommand { get; }
        public DelegateCommand QueryLock4gBandCommand { get; }
        public DelegateCommand Lock5gBandCommand { get; }
        public DelegateCommand QueryLock5gBandCommand { get; }

        /// <summary>统一发送入口：失败时记录错误日志。</summary>
        private async Task SendAsync(string command, int timeoutMs = 3000)
        {
            try
            {
                await _at.SendAsync(command, timeoutMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[NetworkViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
