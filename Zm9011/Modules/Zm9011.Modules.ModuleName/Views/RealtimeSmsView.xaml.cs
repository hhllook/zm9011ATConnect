using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Prism.Navigation;
using Zm9011.Modules.ModuleName.ViewModels;

namespace Zm9011.Modules.ModuleName.Views
{
    /// <summary>
    /// 实时短信页 code-behind：处理消息时间线自动滚动与 ViewModel 事件解绑。
    /// </summary>
    public partial class RealtimeSmsView : UserControl, IDestructible
    {
        private RealtimeSmsViewModel? _viewModel;

        public RealtimeSmsView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => BindViewModel();
            Loaded += (s, e) =>
            {
                BindViewModel();
                _viewModel?.Attach();
                ScrollToBottom();
            };
            Unloaded += (s, e) =>
            {
                _viewModel?.Detach();
                BindViewModel(unsubscribeOnly: true);
            };
        }

        /// <summary>Prism 销毁视图时兜底解绑。</summary>
        public void Destroy()
        {
            _viewModel?.Detach();
            BindViewModel(unsubscribeOnly: true);
        }

        /// <summary>订阅 / 取消订阅 ViewModel 的滚动请求。</summary>
        private void BindViewModel(bool unsubscribeOnly = false)
        {
            if (_viewModel != null)
            {
                _viewModel.ScrollToBottomRequested -= OnScrollRequested;
                _viewModel = null;
            }

            if (unsubscribeOnly)
            {
                return;
            }

            _viewModel = DataContext as RealtimeSmsViewModel;
            if (_viewModel != null)
            {
                _viewModel.ScrollToBottomRequested += OnScrollRequested;
            }
        }

        private void OnScrollRequested()
        {
            Dispatcher.BeginInvoke(new Action(ScrollToBottom), DispatcherPriority.Background);
        }

        /// <summary>滚动到最新一条消息。</summary>
        private void ScrollToBottom()
        {
            if (MessageList.Items.Count > 0)
            {
                MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
            }
        }
    }
}
