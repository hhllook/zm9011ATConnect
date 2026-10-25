using Prism.Ioc;
using Prism.Modularity;
using System.Windows;
using Zm9011.Modules.ModuleName;
using Zm9011.Services;
using Zm9011.Services.Interfaces;
using Zm9011.Views;

namespace Zm9011
{
    /// <summary>
    /// 应用入口：注册串口服务、AT 指令服务、日志服务（单例），
    /// 并加载 AT 调试页面模块。
    /// </summary>
    public partial class App
    {
        protected override Window CreateShell()
        {
            return Container.Resolve<MainWindow>();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 日志/串口/AT 服务全局单例，跨页面共享同一串口会话
            containerRegistry.RegisterSingleton<ILogService, LogService>();
            containerRegistry.RegisterSingleton<ISerialPortService, SerialPortService>();
            containerRegistry.RegisterSingleton<IAtCommandService, AtCommandService>();
        }

        protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
        {
            moduleCatalog.AddModule<AtPagesModule>();
        }
    }
}
