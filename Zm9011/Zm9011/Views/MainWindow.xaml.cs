using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zm9011.ViewModels;

namespace Zm9011.Views
{
    /// <summary>
    /// 主窗口 code-behind：日志区滚动跟随、启动自动重连、按页面显隐底部日志区。
    /// 均为纯 UI 行为，不放进 ViewModel。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>日志列表内部的滚动容器（用于判断用户是否停留在顶部）。</summary>
        private ScrollViewer? _logScrollViewer;

        /// <summary>是否跟随最新日志（用户停留在顶部时为 true）。</summary>
        private bool _followLatest = true;

        /// <summary>是否已触发启动自动重连（只执行一次）。</summary>
        private bool _autoConnectStarted;

        /// <summary>当前绑定的 ViewModel（用于解绑属性/集合事件）。</summary>
        private MainWindowViewModel? _viewModel;

        /// <summary>隐藏日志区前的行高，返回其它页面时恢复。</summary>
        private GridLength _savedLogRowHeight = new GridLength(280);

        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            LogList.Loaded += OnLogListLoaded;
            Loaded += (s, e) =>
            {
                AttachViewModel();
                TryAutoConnect();
            };

            // Prism 的 AutoWireViewModel 可能在 InitializeComponent 内部就设置了 DataContext，
            // 此时 DataContextChanged 已经错过，需要主动绑定一次。
            AttachViewModel();
        }

        /// <summary>窗口加载后尝试自动连接上次成功连接的串口。</summary>
        private void TryAutoConnect()
        {
            if (_autoConnectStarted)
            {
                return;
            }

            if (DataContext is MainWindowViewModel vm)
            {
                _autoConnectStarted = true;
                _ = vm.AutoConnectLastPortAsync();
            }
        }

        private void OnLogListLoaded(object sender, RoutedEventArgs e)
        {
            _logScrollViewer = FindScrollViewer(LogList);
            if (_logScrollViewer != null)
            {
                _logScrollViewer.ScrollChanged += OnLogScrollChanged;
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachViewModel();
            TryAutoConnect();
        }

        /// <summary>绑定当前 ViewModel 的属性与日志集合事件（幂等）。</summary>
        private void AttachViewModel()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ((INotifyCollectionChanged)_viewModel.LogSessions).CollectionChanged -= OnLogSessionsChanged;
                _viewModel = null;
            }

            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel == null)
            {
                return;
            }

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ((INotifyCollectionChanged)_viewModel.LogSessions).CollectionChanged += OnLogSessionsChanged;
            UpdateLogPanelVisibility(_viewModel.IsLogPanelVisible);
        }

        /// <summary>日志模块集合变化：最新模块在索引 0，停留顶部时保持跟随。</summary>
        private void OnLogSessionsChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action != NotifyCollectionChangedAction.Add || !_followLatest)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[0]);
                }
            }), DispatcherPriority.Background);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsLogPanelVisible) && _viewModel != null)
            {
                UpdateLogPanelVisibility(_viewModel.IsLogPanelVisible);
            }
        }

        /// <summary>
        /// 显隐底部“自定义指令 + 收发日志”区域。
        /// 隐藏时记录当前行高，恢复时还原用户拖动后的高度。
        /// </summary>
        private void UpdateLogPanelVisibility(bool visible)
        {
            var logRow = MainAreaGrid.RowDefinitions[3];
            if (visible)
            {
                logRow.MinHeight = 150;
                logRow.Height = _savedLogRowHeight;
                LogSplitter.Visibility = Visibility.Visible;
                LogPanel.Visibility = Visibility.Visible;
                return;
            }

            if (logRow.Height.Value > 0)
            {
                _savedLogRowHeight = logRow.Height;
            }

            logRow.MinHeight = 0;
            logRow.Height = new GridLength(0);
            LogSplitter.Visibility = Visibility.Collapsed;
            LogPanel.Visibility = Visibility.Collapsed;
        }

        private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // VerticalOffset 接近 0 视为停留在顶部，恢复自动跟随
            _followLatest = e.VerticalOffset <= 0.5;
        }

        /// <summary>在可视树中查找 ScrollViewer（ListBox 的模板内部容器）。</summary>
        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
