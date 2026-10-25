using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;
using Zm9011.Modules.ModuleName.Sms;
using Zm9011.Services.Interfaces;

namespace Zm9011.Modules.ModuleName.ViewModels
{
    /// <summary>
    /// 实时短信页面 ViewModel：负责会话展示、选中、验证码操作与回复输入。
    /// 实时接收、PDU 解析与本地持久化由常驻单例 <see cref="RealtimeSmsService"/> 完成，
    /// 页面离开后仍可继续收短信。
    /// </summary>
    public class RealtimeSmsViewModel : BindableBase, INavigationAware
    {
        private readonly HashSet<string> _knownIds = new HashSet<string>();
        private readonly ILogService _log;

        public RealtimeSmsViewModel(RealtimeSmsService service, ILogService log)
        {
            Service = service;
            _log = log;
            Conversations = new ObservableCollection<SmsConversation>();

            BackCommand = new DelegateCommand(() => SelectedConversation = null);
            SendReplyCommand = new DelegateCommand(async () => await SendReplyAsync(),
                    () => HasSelectedConversation && !string.IsNullOrWhiteSpace(ReplyText))
                .ObservesProperty(() => ReplyText)
                .ObservesProperty(() => SelectedConversation);
            CopyNumberCommand = new DelegateCommand(CopyNumber, () => HasSelectedConversation)
                .ObservesProperty(() => SelectedConversation);
            CopyCodeCommand = new DelegateCommand<string>(code => CopyCode(code));
            ToggleRawCommand = new DelegateCommand<SmsMessage>(message =>
            {
                if (message != null)
                {
                    message.IsRawVisible = !message.IsRawVisible;
                }
            });
            InsertCodeCommand = new DelegateCommand(InsertCode, CanInsertCode)
                .ObservesProperty(() => SelectedConversation);
            ClearConversationCommand = new DelegateCommand(ClearConversation, () => HasSelectedConversation)
                .ObservesProperty(() => SelectedConversation);
            ExportCommand = new DelegateCommand(Export);
            OpenStorageFolderCommand = new DelegateCommand(OpenStorageFolder);

            // 先订阅再取快照，避免启动瞬间的新消息遗漏；Id 去重防止重复添加
            Attach();
            LoadConversations();
        }

        /// <summary>常驻实时短信服务（状态、消息接收与持久化）。</summary>
        public RealtimeSmsService Service { get; }

        /// <summary>会话列表（最新会话在上）。</summary>
        public ObservableCollection<SmsConversation> Conversations { get; }

        private SmsConversation? _selectedConversation;
        /// <summary>当前打开的会话。</summary>
        public SmsConversation? SelectedConversation
        {
            get => _selectedConversation;
            set
            {
                if (!SetProperty(ref _selectedConversation, value))
                {
                    return;
                }

                if (value != null)
                {
                    value.MarkRead();
                    Service.SaveAll();
                }

                RaisePropertyChanged(nameof(HasSelectedConversation));
                RaisePropertyChanged(nameof(SelectedSender));
                SendReplyCommand.RaiseCanExecuteChanged();
                CopyNumberCommand.RaiseCanExecuteChanged();
                ClearConversationCommand.RaiseCanExecuteChanged();
                InsertCodeCommand.RaiseCanExecuteChanged();
                RequestScrollToBottom();
            }
        }

        public bool HasSelectedConversation => SelectedConversation != null;

        /// <summary>当前会话对端号码（会话头部显示）。</summary>
        public string SelectedSender => SelectedConversation?.Sender ?? string.Empty;

        private string _replyText = string.Empty;
        /// <summary>回复输入框内容。</summary>
        public string ReplyText
        {
            get => _replyText;
            set
            {
                if (SetProperty(ref _replyText, value))
                {
                    SendReplyCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public DelegateCommand BackCommand { get; }
        public DelegateCommand SendReplyCommand { get; }
        public DelegateCommand CopyNumberCommand { get; }
        public DelegateCommand<string> CopyCodeCommand { get; }
        public DelegateCommand<SmsMessage> ToggleRawCommand { get; }
        public DelegateCommand InsertCodeCommand { get; }
        public DelegateCommand ClearConversationCommand { get; }
        public DelegateCommand ExportCommand { get; }
        public DelegateCommand OpenStorageFolderCommand { get; }

        /// <summary>请求界面滚动到最新一条消息。</summary>
        public event Action? ScrollToBottomRequested;

        /// <summary>导航到页面：若实时接收尚未开启，则自动尝试开启。</summary>
        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            _ = Service.EnsureEnabledAsync();
            SendReplyCommand.RaiseCanExecuteChanged();
            RequestScrollToBottom();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
        }

        /// <summary>页面加载时订阅服务消息事件（幂等）。</summary>
        public void Attach()
        {
            Service.MessageReceived -= OnMessageReceived;
            Service.MessageReceived += OnMessageReceived;
        }

        /// <summary>页面销毁时取消服务事件订阅，避免旧页面重复更新。</summary>
        public void Detach()
        {
            Service.MessageReceived -= OnMessageReceived;
        }

        /// <summary>从服务快照构建会话。</summary>
        private void LoadConversations()
        {
            var stored = Service.GetMessagesSnapshot()
                .OrderBy(m => m.ReceivedAt)
                .ToList();

            foreach (var message in stored)
            {
                _knownIds.Add(message.Id);
                var conversation = GetOrCreateConversation(message.Sender, moveToTop: false);
                conversation.Add(message, markRead: message.IsRead || message.IsOutgoing);
            }

            var ordered = Conversations
                .OrderByDescending(c => c.LastMessage?.ReceivedAt ?? DateTime.MinValue)
                .ToList();
            Conversations.Clear();
            foreach (var conversation in ordered)
            {
                Conversations.Add(conversation);
            }

            if (Conversations.Count > 0)
            {
                SelectedConversation = Conversations[0];
            }
        }

        /// <summary>服务收到新消息（已在 UI 线程）。</summary>
        private void OnMessageReceived(SmsMessage message)
        {
            if (message == null || !_knownIds.Add(message.Id))
            {
                return;
            }

            var conversation = GetOrCreateConversation(message.Sender, moveToTop: true);
            var isSelected = ReferenceEquals(conversation, SelectedConversation);
            conversation.Add(message, markRead: isSelected);

            if (SelectedConversation == null)
            {
                SelectedConversation = conversation;
            }
            else if (isSelected)
            {
                conversation.MarkRead();
                Service.SaveAll();
                RequestScrollToBottom();
            }

            InsertCodeCommand.RaiseCanExecuteChanged();
            SendReplyCommand.RaiseCanExecuteChanged();
        }

        /// <summary>查找或创建会话；moveToTop 时把会话移到列表首位。</summary>
        private SmsConversation GetOrCreateConversation(string sender, bool moveToTop)
        {
            var conversation = Conversations.FirstOrDefault(c => c.Sender == sender);
            if (conversation == null)
            {
                conversation = new SmsConversation(sender);
                if (moveToTop)
                {
                    Conversations.Insert(0, conversation);
                }
                else
                {
                    Conversations.Add(conversation);
                }
                return conversation;
            }

            if (moveToTop)
            {
                var currentIndex = Conversations.IndexOf(conversation);
                if (currentIndex > 0)
                {
                    Conversations.RemoveAt(currentIndex);
                    Conversations.Insert(0, conversation);
                }
            }

            return conversation;
        }

        /// <summary>发送回复短信。</summary>
        private async Task SendReplyAsync()
        {
            var conversation = SelectedConversation;
            var text = ReplyText.Trim();
            if (conversation == null || text.Length == 0)
            {
                return;
            }

            if (await Service.SendReplyAsync(conversation.Sender, text))
            {
                ReplyText = string.Empty;
            }
        }

        /// <summary>复制当前会话号码。</summary>
        private void CopyNumber()
        {
            var number = SelectedSender;
            if (string.IsNullOrEmpty(number))
            {
                return;
            }

            try
            {
                Clipboard.SetText(number);
            }
            catch (Exception ex)
            {
                _log.Error($"[RealtimeSmsViewModel.CopyNumber] 复制号码失败 number={number} error={ex.Message} stack={ex.StackTrace}");
                Service.SetStatusForUser($"复制号码失败：{ex.Message}");
            }
        }

        /// <summary>复制验证码。</summary>
        private void CopyCode(string? code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return;
            }

            try
            {
                Clipboard.SetText(code);
            }
            catch (Exception ex)
            {
                _log.Error($"[RealtimeSmsViewModel.CopyCode] 复制验证码失败 code={code} error={ex.Message} stack={ex.StackTrace}");
                Service.SetStatusForUser($"复制验证码失败：{ex.Message}");
            }
        }

        /// <summary>把当前会话最近一条验证码填入回复框。</summary>
        private void InsertCode()
        {
            var code = SelectedConversation?.Messages
                .LastOrDefault(m => !string.IsNullOrEmpty(m.VerificationCode))
                ?.VerificationCode;
            if (!string.IsNullOrEmpty(code))
            {
                ReplyText = code;
            }
        }

        private bool CanInsertCode()
        {
            return SelectedConversation?.Messages.Any(m => !string.IsNullOrEmpty(m.VerificationCode)) == true;
        }

        /// <summary>清空当前会话（需确认）。</summary>
        private void ClearConversation()
        {
            var conversation = SelectedConversation;
            if (conversation == null)
            {
                return;
            }

            if (MessageBox.Show($"确定清空与 {conversation.Sender} 的全部短信？", "清空会话",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            var ids = conversation.Messages.Select(m => m.Id).ToList();
            Conversations.Remove(conversation);
            SelectedConversation = Conversations.FirstOrDefault();
            Service.RemoveMessages(ids);
        }

        /// <summary>导出全部短信为文本文件。</summary>
        private void Export()
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "文本文件|*.txt",
                    FileName = $"Sms_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var lines = new List<string>();
                foreach (var conversation in Conversations.OrderBy(c => c.LastMessage?.ReceivedAt ?? DateTime.MinValue))
                {
                    lines.Add($"===== {conversation.Sender} =====");
                    foreach (var message in conversation.Messages.OrderBy(m => m.ReceivedAt))
                    {
                        lines.Add($"[{message.ReceivedAt:yyyy-MM-dd HH:mm:ss}] {(message.IsOutgoing ? "我" : message.Sender)}: {message.Body}");
                    }
                    lines.Add(string.Empty);
                }

                File.WriteAllLines(dialog.FileName, lines);
                Service.SetStatusForUser("短信已导出");
            }
            catch (Exception ex)
            {
                _log.Error($"[RealtimeSmsViewModel.Export] 导出短信失败 error={ex.Message} stack={ex.StackTrace}");
                Service.SetStatusForUser($"导出失败：{ex.Message}");
            }
        }

        /// <summary>打开本地短信存储目录。</summary>
        private void OpenStorageFolder()
        {
            try
            {
                Directory.CreateDirectory(SmsStore.FolderPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = SmsStore.FolderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _log.Error($"[RealtimeSmsViewModel.OpenStorageFolder] 打开存储目录失败 path={SmsStore.FolderPath} error={ex.Message} stack={ex.StackTrace}");
                Service.SetStatusForUser($"打开存储目录失败：{ex.Message}");
            }
        }

        /// <summary>请求界面滚动到底部。</summary>
        private void RequestScrollToBottom()
        {
            ScrollToBottomRequested?.Invoke();
        }
    }
}
