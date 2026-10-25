using System.Windows;
using System.Windows.Controls.Primitives;
using Zm9011.Services.Interfaces;

namespace Zm9011.Behaviors
{
    /// <summary>
    /// 按钮动作跟踪器：按钮被点击时，把按钮上的功能名写入 <see cref="AtActionScope.Current"/>。
    /// AT 服务在命令入队时读取该上下文，从而把同一次点击产生的命令与响应归入同一个日志模块。
    /// </summary>
    public static class AtActionTracker
    {
        /// <summary>动作名称（通常绑定到按钮 Content 上的功能名）。</summary>
        public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
            "Title",
            typeof(string),
            typeof(AtActionTracker),
            new PropertyMetadata(null, OnTitleChanged));

        public static string? GetTitle(DependencyObject element) => (string?)element.GetValue(TitleProperty);

        public static void SetTitle(DependencyObject element, string? value) => element.SetValue(TitleProperty, value);

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ButtonBase button)
            {
                return;
            }

            // 先移除再挂载，避免样式重复应用时重复订阅
            button.Click -= OnButtonClick;
            button.Click += OnButtonClick;
        }

        /// <summary>ButtonBase.OnClick 先触发 Click 事件、再执行 Command，因此在这里设置上下文能赶在命令入队前生效。</summary>
        private static void OnButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            var title = GetTitle(element);
            if (!string.IsNullOrWhiteSpace(title))
            {
                AtActionScope.Current = new AtAction(title);
            }
        }
    }
}
