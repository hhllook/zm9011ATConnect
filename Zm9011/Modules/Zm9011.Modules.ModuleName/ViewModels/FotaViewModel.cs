using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// FOTA 页（手册第 6 章）：OMA DM / FTP / HTTP / 本地文件四种升级发起方式、
    /// 升级状态查询、安装结果、可选包确认取消、AT 口升级包推送（+FOTAPUSH 分包传输）。
    /// </summary>
    public class FotaViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public FotaViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            // OMA DM 升级
            StartOmaDmCommand = new DelegateCommand(async () =>
                await SendAsync("AT+ZDM=\"1\"").ConfigureAwait(false));

            // FTP 地址升级
            StartFtpCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZDM=\"{FtpUrl}\"").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(FtpUrl)).ObservesProperty(() => FtpUrl);

            // HTTP/HTTPS 地址升级
            StartHttpCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZDM=\"{HttpUrl}\"").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(HttpUrl)).ObservesProperty(() => HttpUrl);

            // 设备本地文件升级
            StartLocalFileCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+ZDM=\"{LocalFilePath}\"").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(LocalFilePath)).ObservesProperty(() => LocalFilePath);

            // 可选升级包确认 / 取消
            ConfirmDownloadCommand = new DelegateCommand(async () =>
                await SendAsync("AT+ZDM=\"4\"").ConfigureAwait(false));
            CancelDownloadCommand = new DelegateCommand(async () =>
                await SendAsync("AT+ZDM=\"5\"").ConfigureAwait(false));

            // 查询升级状态 / 安装结果 / 清理过程文件
            QueryStatusCommand = new DelegateCommand(async () => await SendAsync("AT+ZDM?").ConfigureAwait(false));
            QueryInstallResultCommand = new DelegateCommand(async () => await SendAsync("AT+ZDM=\"2\"").ConfigureAwait(false));
            ClearFilesCommand = new DelegateCommand(async () => await SendAsync("AT+ZDM=\"3\"").ConfigureAwait(false));

            // AT 口推送升级包
            PushFileCommand = new DelegateCommand(async () => await PushFileAsync().ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(PushLocalPath) && File.Exists(PushLocalPath))
                .ObservesProperty(() => PushLocalPath);
            DeletePushedFileCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+FOTAPUSH=del,{PushModulePath}").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(PushModulePath)).ObservesProperty(() => PushModulePath);
            CheckPushedFileCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+FOTAPUSH=check,{PushModulePath}").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(PushModulePath)).ObservesProperty(() => PushModulePath);

            // 浏览本地升级包
            BrowseCommand = new DelegateCommand(Browse);
        }

        private string _ftpUrl = string.Empty;
        /// <summary>FTP 升级地址：ftp://user:pass@host:port/path。</summary>
        public string FtpUrl { get => _ftpUrl; set => SetProperty(ref _ftpUrl, value); }

        private string _httpUrl = string.Empty;
        /// <summary>HTTP/HTTPS 升级地址。</summary>
        public string HttpUrl { get => _httpUrl; set => SetProperty(ref _httpUrl, value); }

        private string _localFilePath = "/zterw/cache/delta.package";
        /// <summary>设备本地升级包路径（/zterw/cache 下）。</summary>
        public string LocalFilePath { get => _localFilePath; set => SetProperty(ref _localFilePath, value); }

        private string _pushLocalPath = string.Empty;
        /// <summary>待推送的本地升级包文件路径（PC 侧）。</summary>
        public string PushLocalPath { get => _pushLocalPath; set => SetProperty(ref _pushLocalPath, value); }

        private string _pushModulePath = "/zterw/cache/delta.bin";
        /// <summary>推送到模组的目标路径（须 /zterw/cache/ 开头）。</summary>
        public string PushModulePath { get => _pushModulePath; set => SetProperty(ref _pushModulePath, value); }

        private int _pushChunkSize = 512;
        /// <summary>每包数据大小：USB AT 512 / UART AT 1024。</summary>
        public int PushChunkSize { get => _pushChunkSize; set => SetProperty(ref _pushChunkSize, value); }

        private bool _isPushing;
        /// <summary>是否正在推送升级包。</summary>
        public bool IsPushing { get => _isPushing; set => SetProperty(ref _isPushing, value); }

        private double _pushProgress;
        /// <summary>推送进度百分比 0-100。</summary>
        public double PushProgress { get => _pushProgress; set => SetProperty(ref _pushProgress, value); }

        public DelegateCommand StartOmaDmCommand { get; }
        public DelegateCommand StartFtpCommand { get; }
        public DelegateCommand StartHttpCommand { get; }
        public DelegateCommand StartLocalFileCommand { get; }
        public DelegateCommand ConfirmDownloadCommand { get; }
        public DelegateCommand CancelDownloadCommand { get; }
        public DelegateCommand QueryStatusCommand { get; }
        public DelegateCommand QueryInstallResultCommand { get; }
        public DelegateCommand ClearFilesCommand { get; }
        public DelegateCommand PushFileCommand { get; }
        public DelegateCommand DeletePushedFileCommand { get; }
        public DelegateCommand CheckPushedFileCommand { get; }
        public DelegateCommand BrowseCommand { get; }

        /// <summary>打开文件选择对话框选择本地升级包。</summary>
        private void Browse()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "升级包|*.bin;*.package;*.zip;*.tar.gz|所有文件|*.*"
                };
                if (dialog.ShowDialog() == true)
                {
                    PushLocalPath = dialog.FileName;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[FotaViewModel.Browse] 打开文件对话框失败 error={ex.Message} stack={ex.StackTrace}");
            }
        }

        public Array ChunkSizes => new[] { 512, 1024 };

        /// <summary>
        /// AT 口推送升级包流程（手册 6.10）：
        /// set 建文件 → 按 chunk 分包 mid 传输 → end 结束包 → check 校验。
        /// USB AT 每包 ≤512 字节，UART AT ≤1024 字节（HEX 后翻倍，注意串口吞吐）。
        /// </summary>
        private async Task PushFileAsync()
        {
            if (IsPushing)
            {
                return;
            }

            try
            {
                IsPushing = true;
                PushProgress = 0;

                var bytes = await File.ReadAllBytesAsync(PushLocalPath).ConfigureAwait(false);
                var modulePath = EnsureCachePrefix(PushModulePath);
                _log.Info($"[FotaViewModel.PushFileAsync] 开始推送升级包 file={PushLocalPath} size={bytes.Length} chunk={PushChunkSize}");

                // 1. set：建立文件
                var setResult = await _at.SendAsync($"AT+FOTAPUSH=set,{modulePath},{bytes.Length}", 10000).ConfigureAwait(false);
                if (!setResult.Success)
                {
                    _log.Error($"[FotaViewModel.PushFileAsync] 创建文件失败 path={modulePath} resp={setResult.FinalLine}");
                    return;
                }

                // 2. 分包传输：mid 包 + end 包。buf 为 HEX 编码，每包字节数 = chunkSize/2（HEX 后恰好 chunkSize 字符）
                var dataBytesPerPacket = PushChunkSize / 2;
                var totalPackets = (int)Math.Ceiling(bytes.Length / (double)dataBytesPerPacket);
                for (var i = 0; i < totalPackets; i++)
                {
                    var offset = i * dataBytesPerPacket;
                    var count = Math.Min(dataBytesPerPacket, bytes.Length - offset);
                    var hex = ToHex(bytes, offset, count);
                    var isLast = i == totalPackets - 1;
                    var keyword = isLast ? "end" : "mid";

                    var cmd = $"AT+FOTAPUSH={i},{keyword},{count},{hex}";
                    var result = await _at.SendAsync(cmd, 15000).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        _log.Error($"[FotaViewModel.PushFileAsync] 第 {i + 1}/{totalPackets} 包推送失败 keyword={keyword} count={count} resp={result.FinalLine}");
                        return;
                    }

                    PushProgress = Math.Round((i + 1) * 100.0 / totalPackets, 1);
                }

                // 3. check：文件校验
                var checkResult = await _at.SendAsync($"AT+FOTAPUSH=check,{modulePath}", 30000).ConfigureAwait(false);
                if (checkResult.Success)
                {
                    _log.Info($"[FotaViewModel.PushFileAsync] 推送完成并校验通过 path={modulePath} packets={totalPackets}");
                }
                else
                {
                    _log.Error($"[FotaViewModel.PushFileAsync] 校验失败 path={modulePath} resp={checkResult.FinalLine}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[FotaViewModel.PushFileAsync] 推送升级包异常 file={PushLocalPath} error={ex.Message} stack={ex.StackTrace}");
            }
            finally
            {
                IsPushing = false;
            }
        }

        /// <summary>确保模组路径以 /zterw/cache/ 开头（手册 6.10.3 要求）。</summary>
        private static string EnsureCachePrefix(string path)
        {
            if (path.StartsWith("/zterw/cache/", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            if (path.StartsWith("/zterw/cache", StringComparison.OrdinalIgnoreCase))
            {
                return "/zterw/cache/" + path.Substring("/zterw/cache".Length).TrimStart('/');
            }
            return "/zterw/cache/" + path.TrimStart('/');
        }

        /// <summary>字节数组转 HEX 字符串。</summary>
        private static string ToHex(byte[] data, int offset, int count)
        {
            var sb = new StringBuilder(count * 2);
            for (var i = offset; i < offset + count; i++)
            {
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>统一发送入口：失败时记录错误日志。</summary>
        private async Task SendAsync(string command)
        {
            try
            {
                await _at.SendAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"[FotaViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
