using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Commands;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// 短信相关页（手册第 5 章）：模式设置、发送、读取、列表、删除、存储选择、
    /// 新短信通知、短信中心号码、编码参数等。支持 TEXT 与 PDU 双模式。
    /// </summary>
    public class SmsViewModel : BindableBase
    {
        private readonly IAtCommandService _at;
        private readonly ILogService _log;

        public SmsViewModel(IAtCommandService at, ILogService log)
        {
            _at = at;
            _log = log;

            SetModeCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CMGF={SmsMode}").ConfigureAwait(false));
            QueryModeCommand = new DelegateCommand(async () => await SendAsync("AT+CMGF?").ConfigureAwait(false));

            SetCscaCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CSCA=\"{Sca}\"").ConfigureAwait(false),
                () => !string.IsNullOrWhiteSpace(Sca)).ObservesProperty(() => Sca);
            QueryCscaCommand = new DelegateCommand(async () => await SendAsync("AT+CSCA?").ConfigureAwait(false));

            SetCpmsCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CPMS=\"{Mem1}\",\"{Mem2}\",\"{Mem3}\"").ConfigureAwait(false));
            QueryCpmsCommand = new DelegateCommand(async () => await SendAsync("AT+CPMS?").ConfigureAwait(false));

            // 发送短信（两段式：AT+CMGS → ">" 提示符 → 正文 Ctrl-Z）
            SendSmsCommand = new DelegateCommand(async () =>
            {
                try
                {
                    if (SmsMode == 1)
                    {
                        // TEXT 模式
                        var body = SmsText;
                        if (UseUcs2)
                        {
                            // 中文需 UCS2 编码：设置 CSMP dcs=8 并转 HEX
                            await SendAsync("AT+CSMP=17,255,0,8").ConfigureAwait(false);
                            body = ToUcs2Hex(SmsText);
                        }
                        await _at.SendInteractiveAsync($"AT+CMGS=\"{DestAddress}\"", ">", body, 20000).ConfigureAwait(false);
                        if (UseUcs2)
                        {
                            // 恢复默认编码，避免影响后续默认短信（手册 5.2.4 说明）
                            await SendAsync("AT+CSMP=,,0,0").ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // PDU 模式：正文为 PDU 串
                        await _at.SendInteractiveAsync($"AT+CMGS={PduLength}", ">", PduText, 20000).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"[SmsViewModel.SendSmsCommand] 发送短信失败 mode={SmsMode} dest={DestAddress} error={ex.Message} stack={ex.StackTrace}");
                }
            },
            () => !string.IsNullOrWhiteSpace(DestAddress) || !string.IsNullOrWhiteSpace(PduText))
            .ObservesProperty(() => DestAddress).ObservesProperty(() => PduText);

            // 存储短信（+CMGW 同为两段式）
            StoreSmsCommand = new DelegateCommand(async () =>
            {
                try
                {
                    if (SmsMode == 1)
                    {
                        await _at.SendInteractiveAsync($"AT+CMGW=\"{DestAddress}\"", ">", SmsText, 20000).ConfigureAwait(false);
                    }
                    else
                    {
                        await _at.SendInteractiveAsync($"AT+CMGW={PduLength}", ">", PduText, 20000).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"[SmsViewModel.StoreSmsCommand] 存储短信失败 dest={DestAddress} error={ex.Message} stack={ex.StackTrace}");
                }
            });

            // 发送存储区短信（+CMSS，单段命令）
            SendStoredCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CMSS={StoredIndex}").ConfigureAwait(false));

            ListSmsCommand = new DelegateCommand(async () =>
            {
                // TEXT 模式用状态字符串，PDU 模式用数字
                var stat = SmsMode == 1 ? $"\"{TextStat}\"" : PduStat.ToString();
                await SendAsync($"AT+CMGL={stat}").ConfigureAwait(false);
            });
            ReadSmsCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CMGR={ReadIndex}").ConfigureAwait(false));
            DeleteSmsCommand = new DelegateCommand(async () =>
                await SendAsync(DeleteFlag > 0 ? $"AT+CMGD={ReadIndex},{DeleteFlag}" : $"AT+CMGD={ReadIndex}").ConfigureAwait(false));

            SetCnmiCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CNMI={CnmiMode},{CnmiMt},{CnmiBm},{CnmiDs},{CnmiBfr}").ConfigureAwait(false));
            QueryCnmiCommand = new DelegateCommand(async () => await SendAsync("AT+CNMI?").ConfigureAwait(false));
            CnmaCommand = new DelegateCommand(async () => await SendAsync("AT+CNMA").ConfigureAwait(false));

            SetCsmsCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CSMS={CsmsService}").ConfigureAwait(false));
            QueryCsmsCommand = new DelegateCommand(async () => await SendAsync("AT+CSMS?").ConfigureAwait(false));

            SetCsmpCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CSMP={CsmpFo},{CsmpVp},{CsmpPid},{CsmpDcs}").ConfigureAwait(false));
            QueryCsmpCommand = new DelegateCommand(async () => await SendAsync("AT+CSMP?").ConfigureAwait(false));

            SetCsdhCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CSDH={CsdhShow}").ConfigureAwait(false));
            QueryCsdhCommand = new DelegateCommand(async () => await SendAsync("AT+CSDH?").ConfigureAwait(false));

            SetCmmsCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CMMS={CmmsMode}").ConfigureAwait(false));
            QueryCmmsCommand = new DelegateCommand(async () => await SendAsync("AT+CMMS?").ConfigureAwait(false));

            SetCscbCommand = new DelegateCommand(async () =>
                await SendAsync($"AT+CSCB={CscbMode},\"{CscbMids}\",\"{CscbDcss}\"").ConfigureAwait(false));
            QueryCscbCommand = new DelegateCommand(async () => await SendAsync("AT+CSCB?").ConfigureAwait(false));
        }

        private int _smsMode = 1;
        /// <summary>短信模式：0 PDU / 1 TEXT。</summary>
        public int SmsMode { get => _smsMode; set => SetProperty(ref _smsMode, value); }

        private string _destAddress = string.Empty;
        /// <summary>目标手机号码。</summary>
        public string DestAddress { get => _destAddress; set => SetProperty(ref _destAddress, value); }

        private string _smsText = string.Empty;
        /// <summary>短信正文（TEXT 模式）。</summary>
        public string SmsText { get => _smsText; set => SetProperty(ref _smsText, value); }

        private bool _useUcs2;
        /// <summary>TEXT 模式是否使用 UCS2 编码（发送中文必须）。</summary>
        public bool UseUcs2 { get => _useUcs2; set => SetProperty(ref _useUcs2, value); }

        private string _pduText = string.Empty;
        /// <summary>PDU 串（PDU 模式正文）。</summary>
        public string PduText { get => _pduText; set => SetProperty(ref _pduText, value); }

        private int _pduLength;
        /// <summary>PDU 长度（TPDU 字符数/2，即去掉 SMSC 头后的字节数）。</summary>
        public int PduLength { get => _pduLength; set => SetProperty(ref _pduLength, value); }

        private string _sca = string.Empty;
        /// <summary>短信中心号码。</summary>
        public string Sca { get => _sca; set => SetProperty(ref _sca, value); }

        private string _mem1 = "ME";
        /// <summary>读取删除介质：SM / ME。</summary>
        public string Mem1 { get => _mem1; set => SetProperty(ref _mem1, value); }

        private string _mem2 = "ME";
        /// <summary>写入发送介质。</summary>
        public string Mem2 { get => _mem2; set => SetProperty(ref _mem2, value); }

        private string _mem3 = "ME";
        /// <summary>接收存储介质。</summary>
        public string Mem3 { get => _mem3; set => SetProperty(ref _mem3, value); }

        private int _readIndex;
        /// <summary>短信存储索引。</summary>
        public int ReadIndex { get => _readIndex; set => SetProperty(ref _readIndex, value); }

        private int _deleteFlag;
        /// <summary>+CMGD 删除标志：0 删单条 / 1-4 批量删除规则。</summary>
        public int DeleteFlag { get => _deleteFlag; set => SetProperty(ref _deleteFlag, value); }

        private int _storedIndex;
        /// <summary>+CMSS 发送存储短信的索引。</summary>
        public int StoredIndex { get => _storedIndex; set => SetProperty(ref _storedIndex, value); }

        private string _textStat = "ALL";
        /// <summary>TEXT 模式列表状态：REC UNREAD / REC READ / STO UNSENT / STO SENT / ALL。</summary>
        public string TextStat { get => _textStat; set => SetProperty(ref _textStat, value); }

        private int _pduStat = 4;
        /// <summary>PDU 模式列表状态：0-4。</summary>
        public int PduStat { get => _pduStat; set => SetProperty(ref _pduStat, value); }

        private int _cnmiMode = 1;
        /// <summary>+CNMI mode。</summary>
        public int CnmiMode { get => _cnmiMode; set => SetProperty(ref _cnmiMode, value); }

        private int _cnmiMt = 1;
        /// <summary>+CNMI mt：0 不通知 / 1 存储并+CMTI / 2 直接+CMT。</summary>
        public int CnmiMt { get => _cnmiMt; set => SetProperty(ref _cnmiMt, value); }

        private int _cnmiBm;
        /// <summary>+CNMI bm（小区广播，暂不支持）。</summary>
        public int CnmiBm { get => _cnmiBm; set => SetProperty(ref _cnmiBm, value); }

        private int _cnmiDs;
        /// <summary>+CNMI ds：0 不上报回执 / 1 直接+CDS / 2 存储并+CDSI。</summary>
        public int CnmiDs { get => _cnmiDs; set => SetProperty(ref _cnmiDs, value); }

        private int _cnmiBfr;
        /// <summary>+CNMI bfr。</summary>
        public int CnmiBfr { get => _cnmiBfr; set => SetProperty(ref _cnmiBfr, value); }

        private int _csmsService;
        /// <summary>+CSMS service：0 / 1（需要 CNMA 确认）。</summary>
        public int CsmsService { get => _csmsService; set => SetProperty(ref _csmsService, value); }

        private int _csmpFo = 17;
        /// <summary>+CSMP fo。</summary>
        public int CsmpFo { get => _csmpFo; set => SetProperty(ref _csmpFo, value); }

        private int _csmpVp = 167;
        /// <summary>+CSMP vp 有效期。</summary>
        public int CsmpVp { get => _csmpVp; set => SetProperty(ref _csmpVp, value); }

        private int _csmpPid;
        /// <summary>+CSMP pid。</summary>
        public int CsmpPid { get => _csmpPid; set => SetProperty(ref _csmpPid, value); }

        private int _csmpDcs;
        /// <summary>+CSMP dcs：0 GSM7 / 4 8bit / 8 UCS2。</summary>
        public int CsmpDcs { get => _csmpDcs; set => SetProperty(ref _csmpDcs, value); }

        private int _csdhShow;
        /// <summary>+CSDH show。</summary>
        public int CsdhShow { get => _csdhShow; set => SetProperty(ref _csdhShow, value); }

        private int _cmmsMode;
        /// <summary>+CMMS n：0 不支持 / 1 / 2 链路保持。</summary>
        public int CmmsMode { get => _cmmsMode; set => SetProperty(ref _cmmsMode, value); }

        private int _cscbMode;
        /// <summary>+CSCB mode：0 接受 / 1 拒绝。</summary>
        public int CscbMode { get => _cscbMode; set => SetProperty(ref _cscbMode, value); }

        private string _cscbMids = string.Empty;
        /// <summary>+CSCB mids。</summary>
        public string CscbMids { get => _cscbMids; set => SetProperty(ref _cscbMids, value); }

        private string _cscbDcss = string.Empty;
        /// <summary>+CSCB dcss。</summary>
        public string CscbDcss { get => _cscbDcss; set => SetProperty(ref _cscbDcss, value); }

        public DelegateCommand SetModeCommand { get; }
        public DelegateCommand QueryModeCommand { get; }
        public DelegateCommand SetCscaCommand { get; }
        public DelegateCommand QueryCscaCommand { get; }
        public DelegateCommand SetCpmsCommand { get; }
        public DelegateCommand QueryCpmsCommand { get; }
        public DelegateCommand SendSmsCommand { get; }
        public DelegateCommand StoreSmsCommand { get; }
        public DelegateCommand SendStoredCommand { get; }
        public DelegateCommand ListSmsCommand { get; }
        public DelegateCommand ReadSmsCommand { get; }
        public DelegateCommand DeleteSmsCommand { get; }
        public DelegateCommand SetCnmiCommand { get; }
        public DelegateCommand QueryCnmiCommand { get; }
        public DelegateCommand CnmaCommand { get; }
        public DelegateCommand SetCsmsCommand { get; }
        public DelegateCommand QueryCsmsCommand { get; }
        public DelegateCommand SetCsmpCommand { get; }
        public DelegateCommand QueryCsmpCommand { get; }
        public DelegateCommand SetCsdhCommand { get; }
        public DelegateCommand QueryCsdhCommand { get; }
        public DelegateCommand SetCmmsCommand { get; }
        public DelegateCommand QueryCmmsCommand { get; }
        public DelegateCommand SetCscbCommand { get; }
        public DelegateCommand QueryCscbCommand { get; }

        public Array TextStats => new[] { "REC UNREAD", "REC READ", "STO UNSENT", "STO SENT", "ALL" };
        public Array MemOptions => new[] { "SM", "ME" };

        /// <summary>把文本转为 UCS2 HEX 串（每字符 4 个十六进制数字，手册 5.2.4 中文发送格式）。</summary>
        private static string ToUcs2Hex(string text)
        {
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                sb.Append(((int)ch).ToString("X4"));
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
                _log.Error($"[SmsViewModel.SendAsync] 发送命令失败 command={command} error={ex.Message} stack={ex.StackTrace}");
            }
        }
    }
}
