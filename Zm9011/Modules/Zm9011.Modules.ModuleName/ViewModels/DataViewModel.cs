using System;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// 数据相关页（手册第 4 章）：联网/断网/自动拨号、PDP 会话参数（APN）、
    /// 流量统计、IP 地址查询。USB 拨号与 UART 拨号参数有差异，分两组。
    /// </summary>
    public class DataViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public DataViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            // USB 模式联网（4.1）
            DialCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZIPCALL={ZipcallState}").ConfigureAwait(false));
            QueryDialCommand = new DelegateCommand(async () => await SendAsync("AT+ZIPCALL?").ConfigureAwait(false));

            // PDP 会话参数（4.2）
            SetZipcfgCommand = new DelegateCommand(async () =>
            {
                // 组装：AT+ZIPCFG=<APN>,<PDP_type>[,<username>,<password>,<auth_type>[,<data_type>]]
                var sb = new System.Text.StringBuilder($"AT+ZIPCFG={Apn},{PdpType}");
                if (!string.IsNullOrEmpty(DataType))
                {
                    sb.Append($",,," + (int)AuthType + $",{DataType}");
                }
                else if (!string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password) || AuthType > 0)
                {
                    sb.Append($",{Username},{Password},{(int)AuthType}");
                }
                await SendAsync(sb.ToString()).ConfigureAwait(false);
            });
            QueryZipcfgCommand = new DelegateCommand(async () => await SendAsync("AT+ZIPCFG?").ConfigureAwait(false));

            // 流量统计（4.3）
            QueryDcntCommand = new DelegateCommand(async () => await SendAsync("AT+ZDCNT?").ConfigureAwait(false));

            // UART 模式联网（4.4）
            UartDialCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZIPCALL={UartZipcallState},{UartCid}").ConfigureAwait(false));
            QueryUartDialCommand = new DelegateCommand(async () => await SendAsync("AT+ZIPCALL?").ConfigureAwait(false));

            // UART 模式 PDP 参数（4.5）：注意设置后需 CFUN 4→1 重启生效
            SetUartZipcfgCommand = new DelegateCommand(async () =>
            {
                await SendAsync($"AT+ZIPCFG={UartApn},{UartPdpType},{UartUsername},{UartPassword},{(int)UartAuthType},{UartCid}").ConfigureAwait(false);
            });
            QueryUartZipcfgCommand = new DelegateCommand(async () => await SendAsync($"AT+ZIPCFG={UartCid}").ConfigureAwait(false));

            // IP 地址查询（4.6）
            QueryIpAddrCommand = new DelegateCommand(async () =>
                await SendAsync(Cid > 0 ? $"AT+CGPADDR={Cid}" : "AT+CGPADDR").ConfigureAwait(false));
        }

        private int _zipcallState = 1;
        /// <summary>+ZIPCALL 参数：0 去激活 / 1 激活 / 2 关自动拨号 / 3 开自动拨号。</summary>
        public int ZipcallState { get => _zipcallState; set => SetProperty(ref _zipcallState, value); }

        private string _apn = string.Empty;
        /// <summary>APN 接入点名。</summary>
        public string Apn { get => _apn; set => SetProperty(ref _apn, value); }

        private int _pdpType = 1;
        /// <summary>PDP 类型：1 IPv4 / 2 IPv6 / 3 IPv4v6。</summary>
        public int PdpType { get => _pdpType; set => SetProperty(ref _pdpType, value); }

        private string _username = string.Empty;
        /// <summary>鉴权用户名。</summary>
        public string Username { get => _username; set => SetProperty(ref _username, value); }

        private string _password = string.Empty;
        /// <summary>鉴权密码。</summary>
        public string Password { get => _password; set => SetProperty(ref _password, value); }

        private AuthTypeMode _authType = AuthTypeMode.None;
        /// <summary>鉴权类型：0 None / 1 PAP / 2 CHAP。</summary>
        public AuthTypeMode AuthType { get => _authType; set => SetProperty(ref _authType, value); }

        private string _dataType = string.Empty;
        /// <summary>数据类型：priv-ip / pub-ip / pas-pub-ip（桥接模式），可空。</summary>
        public string DataType { get => _dataType; set => SetProperty(ref _dataType, value); }

        private int _uartZipcallState = 1;
        /// <summary>UART 联网状态：0 去激活 / 1 激活 / 2 激活中 / 3 去激活中。</summary>
        public int UartZipcallState { get => _uartZipcallState; set => SetProperty(ref _uartZipcallState, value); }

        private int _uartCid = 1;
        /// <summary>UART 通道号：1-4。</summary>
        public int UartCid { get => _uartCid; set => SetProperty(ref _uartCid, value); }

        private string _uartApn = string.Empty;
        /// <summary>UART 模式 APN。</summary>
        public string UartApn { get => _uartApn; set => SetProperty(ref _uartApn, value); }

        private int _uartPdpType = 1;
        /// <summary>UART 模式 PDP 类型：1 IPv4 / 2 IPv6 / 3 IPv4v6。</summary>
        public int UartPdpType { get => _uartPdpType; set => SetProperty(ref _uartPdpType, value); }

        private string _uartUsername = string.Empty;
        /// <summary>UART 模式用户名。</summary>
        public string UartUsername { get => _uartUsername; set => SetProperty(ref _uartUsername, value); }

        private string _uartPassword = string.Empty;
        /// <summary>UART 模式密码。</summary>
        public string UartPassword { get => _uartPassword; set => SetProperty(ref _uartPassword, value); }

        private AuthTypeMode _uartAuthType = AuthTypeMode.None;
        /// <summary>UART 模式鉴权类型。</summary>
        public AuthTypeMode UartAuthType { get => _uartAuthType; set => SetProperty(ref _uartAuthType, value); }

        private int _cid = 1;
        /// <summary>+CGPADDR 的 cid，取值 1。</summary>
        public int Cid { get => _cid; set => SetProperty(ref _cid, value); }

        public DelegateCommand DialCommand { get; }
        public DelegateCommand QueryDialCommand { get; }
        public DelegateCommand SetZipcfgCommand { get; }
        public DelegateCommand QueryZipcfgCommand { get; }
        public DelegateCommand QueryDcntCommand { get; }
        public DelegateCommand UartDialCommand { get; }
        public DelegateCommand QueryUartDialCommand { get; }
        public DelegateCommand SetUartZipcfgCommand { get; }
        public DelegateCommand QueryUartZipcfgCommand { get; }
        public DelegateCommand QueryIpAddrCommand { get; }

        /// <summary>鉴权类型下拉数据源。</summary>
        public Array AuthTypes => Enum.GetValues(typeof(AuthTypeMode));

        /// <summary>统一发送入口：失败时记录错误日志。</summary>
        private async Task SendAsync(string command)
        {
            try
            {
                await _at.SendAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[DataViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }

    /// <summary>PDP 鉴权类型（对应 +ZIPCFG 的 auth_type）。</summary>
    public enum AuthTypeMode
    {
        /// <summary>不鉴权。</summary>
        None = 0,
        /// <summary>PAP 密码鉴权。</summary>
        Pap = 1,
        /// <summary>CHAP 挑战握手鉴权。</summary>
        Chap = 2
    }
}
