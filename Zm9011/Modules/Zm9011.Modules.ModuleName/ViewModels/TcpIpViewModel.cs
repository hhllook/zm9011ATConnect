using System;
using System.Text;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// TCP/IP 协议功能页（手册第 8 章）：服务参数配置、连接开关、数据收发（非透传）、
    /// 透传模式、FTP、DHCP 网关、TCP Server 侦听接受/拒绝。依赖 URN 事件（+MYURCREAD 等）提示收发。
    /// </summary>
    public class TcpIpViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public TcpIpViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            // 服务参数 +MYNETSRV
            SetSrvCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYNETSRV={SrvChannel},{SrvSocketId},{NetType},{ViewModelMode},\"{Address}\",{ReadMode}").ConfigureAwait(false));
            QuerySrvCommand = new DelegateCommand(async () => await SendAsync("AT+MYNETSRV?").ConfigureAwait(false));

            // 开启 / 查询 / 关闭
            OpenCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYNETOPEN={OpenSocketId}", 15000).ConfigureAwait(false));
            QueryOpenCommand = new DelegateCommand(async () => await SendAsync("AT+MYNETOPEN?").ConfigureAwait(false));
            CloseCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYNETCLOSE={OpenSocketId}").ConfigureAwait(false));

            // 非透传读数据：+MYNETREAD=<SocketID>,<len>
            ReadDataCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYNETREAD={ReadSocketId},{ReadLength}", 15000).ConfigureAwait(false));

            // 非透传写数据：+MYNETWRITE 两段式（命令 → "+MYNETWRITE:" 触发行 → 正文 → OK）
            WriteDataCommand = new DelegateCommand(async () =>
            {
                try
                {
                    var body = WriteHex ? ToHex(WriteText) : WriteText;
                    var len = body.Length;
                    await _at.SendInteractiveAsync($"AT+MYNETWRITE={WriteSocketId},{len}", "+MYNETWRITE:", body, 20000).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"[TcpIpViewModel.WriteDataCommand] 发送数据失败 socketId={WriteSocketId} error={ex.Message} stack={ex.StackTrace}");
                }
            },
            () => !string.IsNullOrEmpty(WriteText)).ObservesProperty(() => WriteText);

            // 透传模式：+MYNETCREATE → CONNECT → 原始数据直写；+++ 退出
            CreateTransparentCommand = new DelegateCommand(async () =>
            {
                try
                {
                    var cmd = AutoReconnect
                        ? $"AT+MYNETCREATE={CreateChannel},{CreateNetType},{CreateSocketId},\"{CreateAddress}\",{CreatePort},,1"
                        : $"AT+MYNETCREATE={CreateChannel},{CreateNetType},{CreateSocketId},\"{CreateAddress}\",{CreatePort}";
                    // CONNECT 作为终止符（connectIsFinal），收到即已进入透传
                    await _at.SendInteractiveAsync(cmd, "CONNECT", null, 20000, connectIsFinal: true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"[TcpIpViewModel.CreateTransparentCommand] 建立透传失败 channel={CreateChannel} error={ex.Message} stack={ex.StackTrace}");
                }
            });
            SendTransparentDataCommand = new DelegateCommand(async () =>
            {
                // 透传模式下直接写原始文本
                _at.SendRaw(WriteText);
                await Task.CompletedTask.ConfigureAwait(false);
            });
            ExitTransparentCommand = new DelegateCommand(async () =>
            {
                // +++ 前后需要 1s 静默期
                await Task.Delay(1000).ConfigureAwait(false);
                _at.SendRaw("+++");
                await Task.Delay(1000).ConfigureAwait(false);
            });

            // TCP Server：+MYNETACCEPT 接受/拒绝客户端
            AcceptClientCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYNETACCEPT={ClientSocketId},{AcceptAction},{TransportMode}").ConfigureAwait(false));

            // DHCP 网关 +ZDHCPCFG
            SetDhcpCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZDHCPCFG=\"{DhcpServer}\",\"{DhcpMask}\",\"{DhcpStart}\",\"{DhcpEnd}\",{DhcpLease}").ConfigureAwait(false));
            QueryDhcpCommand = new DelegateCommand(async () => await SendAsync("AT+ZDHCPCFG?").ConfigureAwait(false));

            // FTP
            FtpOpenCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYFTPOPEN={FtpChannel},\"{FtpHost}\",\"{FtpUser}\",\"{FtpPassword}\",{FtpMode},{FtpTimeout},{FtpType}", 30000).ConfigureAwait(false));
            FtpSizeCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYFTPSIZE=\"{FtpFile}\"", 15000).ConfigureAwait(false));
            FtpGetCommand = new DelegateCommand(async () =>
            {
                try
                {
                    // CONNECT 后跟文件内容再 OK：connectIsFinal=false，服务把内容并进响应行
                    await _at.SendInteractiveAsync($"AT+MYFTPGET=\"{FtpFile}\"", "CONNECT", null, 30000, connectIsFinal: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"[TcpIpViewModel.FtpGetCommand] FTP 下载失败 file={FtpFile} error={ex.Message} stack={ex.StackTrace}");
                }
            });
            FtpPutCommand = new DelegateCommand(async () =>
            {
                try
                {
                    await _at.SendInteractiveAsync($"AT+MYFTPPUT=\"{FtpFile}\",{FtpPutData.Length},{FtpPutEof}", "CONNECT", FtpPutData, 30000, connectIsFinal: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"[TcpIpViewModel.FtpPutCommand] FTP 上传失败 file={FtpFile} error={ex.Message} stack={ex.StackTrace}");
                }
            }, () => !string.IsNullOrEmpty(FtpPutData)).ObservesProperty(() => FtpPutData);
            FtpCloseCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+MYFTPCLOSE={FtpChannel}").ConfigureAwait(false));
        }

        // ---- +MYNETSRV 参数 ----
        private int _srvChannel = 1;
        /// <summary>通道号（= PDP cid），1-4。</summary>
        public int SrvChannel { get => _srvChannel; set => SetProperty(ref _srvChannel, value); }

        private int _srvSocketId = 0;
        /// <summary>Socket ID，0-5。</summary>
        public int SrvSocketId { get => _srvSocketId; set => SetProperty(ref _srvSocketId, value); }

        private int _netType;
        /// <summary>网络类型：0 TCP Client / 1 TCP Server / 2 UDP Client。</summary>
        public int NetType { get => _netType; set => SetProperty(ref _netType, value); }

        private int _viewModelMode;
        /// <summary>数据显示模式：0 HEX / 1 TEXT。</summary>
        public int ViewModelMode { get => _viewModelMode; set => SetProperty(ref _viewModelMode, value); }

        private string _address = "127.0.0.1:5000";
        /// <summary>服务器地址:端口（TCP Server 时 IP 为 127.0.0.1 表示侦听）。</summary>
        public string Address { get => _address; set => SetProperty(ref _address, value); }

        private int _readMode = 1;
        /// <summary>读取模式：0 被动（需 MYNETREAD）/ 1 主动。</summary>
        public int ReadMode { get => _readMode; set => SetProperty(ref _readMode, value); }

        // ---- 连接参数 ----
        private int _openSocketId;
        /// <summary>操作的 Socket ID。</summary>
        public int OpenSocketId { get => _openSocketId; set => SetProperty(ref _openSocketId, value); }

        private int _readSocketId;
        /// <summary>读取数据的 Socket ID。</summary>
        public int ReadSocketId { get => _readSocketId; set => SetProperty(ref _readSocketId, value); }

        private int _readLength = 1460;
        /// <summary>单次读取最大长度 1-1460。</summary>
        public int ReadLength { get => _readLength; set => SetProperty(ref _readLength, value); }

        private int _writeSocketId;
        /// <summary>写数据的 Socket ID。</summary>
        public int WriteSocketId { get => _writeSocketId; set => SetProperty(ref _writeSocketId, value); }

        private string _writeText = string.Empty;
        /// <summary>待发送的数据内容。</summary>
        public string WriteText { get => _writeText; set => SetProperty(ref _writeText, value); }

        private bool _writeHex;
        /// <summary>发送内容是否按 HEX 编码（手册 8.1.3 view mode：HEX 模式下 "123" 发 "313233"）。</summary>
        public bool WriteHex { get => _writeHex; set => SetProperty(ref _writeHex, value); }

        // ---- 透传参数 ----
        private int _createChannel = 2;
        /// <summary>透传通道号 1-4。</summary>
        public int CreateChannel { get => _createChannel; set => SetProperty(ref _createChannel, value); }

        private int _createNetType;
        /// <summary>透传网络类型：0 TCP Client / 1 TCP Server / 2 UDP Client。</summary>
        public int CreateNetType { get => _createNetType; set => SetProperty(ref _createNetType, value); }

        private int _createSocketId = 2;
        /// <summary>透传 Socket ID 0-5。</summary>
        public int CreateSocketId { get => _createSocketId; set => SetProperty(ref _createSocketId, value); }

        private string _createAddress = "58.250.204.23";
        /// <summary>透传服务器地址。</summary>
        public string CreateAddress { get => _createAddress; set => SetProperty(ref _createAddress, value); }

        private int _createPort = 1095;
        /// <summary>透传服务器端口。</summary>
        public int CreatePort { get => _createPort; set => SetProperty(ref _createPort, value); }

        private bool _autoReconnect;
        /// <summary>断线自动重连（+MYNETCREATE 第 7 参数）。</summary>
        public bool AutoReconnect { get => _autoReconnect; set => SetProperty(ref _autoReconnect, value); }

        // ---- TCP Server 参数 ----
        private int _clientSocketId = 1;
        /// <summary>客户端 Socket ID（+MYURCCLIENT 上报的）。</summary>
        public int ClientSocketId { get => _clientSocketId; set => SetProperty(ref _clientSocketId, value); }

        private int _acceptAction;
        /// <summary>0 接受 / 1 拒绝。</summary>
        public int AcceptAction { get => _acceptAction; set => SetProperty(ref _acceptAction, value); }

        private int _transportMode;
        /// <summary>0 非透传 / 1 透传。</summary>
        public int TransportMode { get => _transportMode; set => SetProperty(ref _transportMode, value); }

        // ---- DHCP 参数 ----
        private string _dhcpServer = "192.168.0.1";
        public string DhcpServer { get => _dhcpServer; set => SetProperty(ref _dhcpServer, value); }

        private string _dhcpMask = "255.255.255.0";
        public string DhcpMask { get => _dhcpMask; set => SetProperty(ref _dhcpMask, value); }

        private string _dhcpStart = "192.168.0.100";
        public string DhcpStart { get => _dhcpStart; set => SetProperty(ref _dhcpStart, value); }

        private string _dhcpEnd = "192.168.0.200";
        public string DhcpEnd { get => _dhcpEnd; set => SetProperty(ref _dhcpEnd, value); }

        private int _dhcpLease = 12;
        /// <summary>租约时间（小时）。</summary>
        public int DhcpLease { get => _dhcpLease; set => SetProperty(ref _dhcpLease, value); }

        // ---- FTP 参数 ----
        private int _ftpChannel = 1;
        public int FtpChannel { get => _ftpChannel; set => SetProperty(ref _ftpChannel, value); }

        private string _ftpHost = string.Empty;
        /// <summary>FTP 服务器 IP/URL:端口。</summary>
        public string FtpHost { get => _ftpHost; set => SetProperty(ref _ftpHost, value); }

        private string _ftpUser = string.Empty;
        public string FtpUser { get => _ftpUser; set => SetProperty(ref _ftpUser, value); }

        private string _ftpPassword = string.Empty;
        public string FtpPassword { get => _ftpPassword; set => SetProperty(ref _ftpPassword, value); }

        private int _ftpMode = 1;
        /// <summary>0 主动 / 1 被动（默认）。</summary>
        public int FtpMode { get => _ftpMode; set => SetProperty(ref _ftpMode, value); }

        private int _ftpTimeout = 30;
        /// <summary>空闲超时秒 5-180。</summary>
        public int FtpTimeout { get => _ftpTimeout; set => SetProperty(ref _ftpTimeout, value); }

        private int _ftpType;
        /// <summary>0 二进制（默认）/ 1 文本。</summary>
        public int FtpType { get => _ftpType; set => SetProperty(ref _ftpType, value); }

        private string _ftpFile = string.Empty;
        /// <summary>FTP 文件名。</summary>
        public string FtpFile { get => _ftpFile; set => SetProperty(ref _ftpFile, value); }

        private string _ftpPutData = string.Empty;
        /// <summary>FTP 上传内容（单次 ≤3072 字节）。</summary>
        public string FtpPutData { get => _ftpPutData; set => SetProperty(ref _ftpPutData, value); }

        private int _ftpPutEof;
        /// <summary>0 起始包 / 1 最后包。</summary>
        public int FtpPutEof { get => _ftpPutEof; set => SetProperty(ref _ftpPutEof, value); }

        public DelegateCommand SetSrvCommand { get; }
        public DelegateCommand QuerySrvCommand { get; }
        public DelegateCommand OpenCommand { get; }
        public DelegateCommand QueryOpenCommand { get; }
        public DelegateCommand CloseCommand { get; }
        public DelegateCommand ReadDataCommand { get; }
        public DelegateCommand WriteDataCommand { get; }
        public DelegateCommand CreateTransparentCommand { get; }
        public DelegateCommand SendTransparentDataCommand { get; }
        public DelegateCommand ExitTransparentCommand { get; }
        public DelegateCommand AcceptClientCommand { get; }
        public DelegateCommand SetDhcpCommand { get; }
        public DelegateCommand QueryDhcpCommand { get; }
        public DelegateCommand FtpOpenCommand { get; }
        public DelegateCommand FtpSizeCommand { get; }
        public DelegateCommand FtpGetCommand { get; }
        public DelegateCommand FtpPutCommand { get; }
        public DelegateCommand FtpCloseCommand { get; }

        /// <summary>文本转 HEX（TCP/IP HEX 模式）。</summary>
        private static string ToHex(string text)
        {
            var sb = new StringBuilder(text.Length * 2);
            foreach (var b in Encoding.ASCII.GetBytes(text))
            {
                sb.Append(b.ToString("X2"));
            }
            return sb.ToString();
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
                _log.Error($"[TcpIpViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
