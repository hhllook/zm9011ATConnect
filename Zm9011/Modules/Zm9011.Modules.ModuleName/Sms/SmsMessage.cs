using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prism.Mvvm;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.Sms
{
    /// <summary>一条已接收或已发送的短信。</summary>
    public class SmsMessage : BindableBase
    {
        private bool _isRawVisible;

        /// <summary>消息唯一标识。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>对端号码（接收时为发送方，发送时为接收方）。</summary>
        public string Sender { get; set; } = string.Empty;

        /// <summary>短信正文。</summary>
        public string Body { get; set; } = string.Empty;

        /// <summary>短信时间（优先使用短信中心时间）。</summary>
        public DateTime ReceivedAt { get; set; } = DateTime.Now;

        /// <summary>是否为本地发出的短信。</summary>
        public bool IsOutgoing { get; set; }

        /// <summary>是否已读。</summary>
        public bool IsRead { get; set; }

        /// <summary>识别出的验证码（无则为 null）。</summary>
        public string? VerificationCode { get; set; }

        /// <summary>验证码标签，如“密码”“验证码”。</summary>
        public string? Tag { get; set; }

        /// <summary>短信签名中的服务名称，如“深圳一流网络技术”。</summary>
        public string? ServiceName { get; set; }

        /// <summary>原始响应（AT+CMGR 的头部与 PDU / 文本）。</summary>
        public string RawText { get; set; } = string.Empty;

        /// <summary>模组存储介质（SM / ME）。</summary>
        public string Storage { get; set; } = string.Empty;

        /// <summary>模组存储索引。</summary>
        public int StorageIndex { get; set; }

        /// <summary>是否展开原文（纯 UI 状态，不持久化）。</summary>
        [JsonIgnore]
        public bool IsRawVisible
        {
            get => _isRawVisible;
            set
            {
                if (SetProperty(ref _isRawVisible, value))
                {
                    RaisePropertyChanged(nameof(RawLinkText));
                }
            }
        }

        /// <summary>是否识别出验证码。</summary>
        [JsonIgnore]
        public bool IsVerification => !string.IsNullOrEmpty(VerificationCode);

        /// <summary>时间（HH:mm）。</summary>
        [JsonIgnore]
        public string TimeText => ReceivedAt.ToString("HH:mm");

        /// <summary>完整时间（MM-dd HH:mm）。</summary>
        [JsonIgnore]
        public string FullTimeText => ReceivedAt.ToString("MM-dd HH:mm");

        /// <summary>会话列表中的正文预览。</summary>
        [JsonIgnore]
        public string BodyPreview => string.IsNullOrWhiteSpace(Body)
            ? "(空短信)"
            : Body.Replace("\r", " ").Replace("\n", " ");

        /// <summary>原文链接文本。</summary>
        [JsonIgnore]
        public string RawLinkText => IsRawVisible ? "收起原文" : "查看原文";
    }

    /// <summary>按对端号码聚合的短信会话。</summary>
    public class SmsConversation : BindableBase
    {
        private int _unreadCount;

        public SmsConversation(string sender)
        {
            Sender = sender;
        }

        /// <summary>对端号码。</summary>
        public string Sender { get; }

        /// <summary>会话内消息，按时间正序（最新在底部）。</summary>
        public ObservableCollection<SmsMessage> Messages { get; } = new ObservableCollection<SmsMessage>();

        /// <summary>未读数量。</summary>
        public int UnreadCount
        {
            get => _unreadCount;
            private set
            {
                if (SetProperty(ref _unreadCount, value))
                {
                    RaisePropertyChanged(nameof(HasUnread));
                    RaisePropertyChanged(nameof(UnreadText));
                }
            }
        }

        [JsonIgnore]
        public bool HasUnread => UnreadCount > 0;

        [JsonIgnore]
        public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();

        /// <summary>最新一条消息。</summary>
        [JsonIgnore]
        public SmsMessage? LastMessage => Messages.Count > 0 ? Messages[Messages.Count - 1] : null;

        /// <summary>会话列表预览文本。</summary>
        [JsonIgnore]
        public string Preview => LastMessage?.BodyPreview ?? string.Empty;

        /// <summary>会话列表时间。</summary>
        [JsonIgnore]
        public string TimeText => LastMessage?.TimeText ?? string.Empty;

        /// <summary>按时间顺序插入消息。</summary>
        public void Add(SmsMessage message, bool markRead)
        {
            var index = Messages.Count;
            while (index > 0 && Messages[index - 1].ReceivedAt > message.ReceivedAt)
            {
                index--;
            }
            Messages.Insert(index, message);

            if (markRead || message.IsOutgoing)
            {
                message.IsRead = true;
            }
            else if (!message.IsOutgoing)
            {
                UnreadCount++;
            }

            RaisePropertyChanged(nameof(LastMessage));
            RaisePropertyChanged(nameof(Preview));
            RaisePropertyChanged(nameof(TimeText));
        }

        /// <summary>全部标记为已读。</summary>
        public void MarkRead()
        {
            foreach (var message in Messages)
            {
                message.IsRead = true;
            }
            UnreadCount = 0;
        }
    }

    /// <summary>短信本地存储：%LOCALAPPDATA%\Zm9011\received_sms.json。</summary>
    public static class SmsStore
    {
        private static readonly object Sync = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>存储目录。</summary>
        public static string FolderPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zm9011");

        /// <summary>存储文件完整路径。</summary>
        public static string FilePath { get; } = Path.Combine(FolderPath, "received_sms.json");

        /// <summary>读取本地短信；文件不存在或损坏时返回空列表。</summary>
        public static List<SmsMessage> Load(ILogService log)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new List<SmsMessage>();
                }

                var json = File.ReadAllText(FilePath);
                var messages = JsonSerializer.Deserialize<List<SmsMessage>>(json, JsonOptions) ?? new List<SmsMessage>();
                log.Info($"[SmsStore.Load] 读取本地短信成功 file={FilePath} count={messages.Count}");
                return messages;
            }
            catch (Exception ex)
            {
                log.Error($"[SmsStore.Load] 读取本地短信失败 file={FilePath} error={ex.Message} stack={ex.StackTrace}");
                return new List<SmsMessage>();
            }
        }

        /// <summary>保存本地短信（先写临时文件再替换，避免写入中断损坏数据）。</summary>
        public static void Save(IEnumerable<SmsMessage> messages, ILogService log)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(FolderPath);
                    var json = JsonSerializer.Serialize(messages.ToList(), JsonOptions);
                    var tempPath = FilePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, FilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    log.Error($"[SmsStore.Save] 保存本地短信失败 file={FilePath} error={ex.Message} stack={ex.StackTrace}");
                }
            }
        }
    }
}
