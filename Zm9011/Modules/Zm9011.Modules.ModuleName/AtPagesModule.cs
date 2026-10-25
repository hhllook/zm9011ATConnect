using Prism.Ioc;
using Prism.Modularity;
using Prism.Regions;
using Zm9011.Core;
using Zm9011.Modules.ModuleName.Views;

namespace Zm9011.Modules.ModuleName
{
    /// <summary>
    /// AT 调试页面模块：注册 9 个功能页（设备信息与设置/网络与信号/数据联网/短信管理/实时短信/
    /// 固件升级/SIM 卡与扩展/TCP/IP 通信/串口设置）到导航区域，启动后默认显示设备信息与设置页。
    /// </summary>
    public class AtPagesModule : IModule
    {
        private readonly IRegionManager _regionManager;

        public AtPagesModule(IRegionManager regionManager)
        {
            _regionManager = regionManager;
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            // 启动即创建实时短信服务：串口连接成功后自动开启 +CNMI，页面未打开也能收短信
            containerProvider.Resolve<Sms.RealtimeSmsService>();
            _regionManager.RequestNavigate(RegionNames.ContentRegion, ViewKeys.General);
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterForNavigation<Views.GeneralView>();
            containerRegistry.RegisterForNavigation<Views.NetworkView>();
            containerRegistry.RegisterForNavigation<Views.DataView>();
            containerRegistry.RegisterForNavigation<Views.SmsView>();
            // 实时接收/持久化服务必须常驻；页面 ViewModel 按导航创建
            containerRegistry.RegisterSingleton<Sms.RealtimeSmsService>();
            containerRegistry.RegisterForNavigation<Views.RealtimeSmsView>();
            containerRegistry.RegisterForNavigation<Views.FotaView>();
            containerRegistry.RegisterForNavigation<Views.ZteExtView>();
            containerRegistry.RegisterForNavigation<Views.TcpIpView>();
            containerRegistry.RegisterForNavigation<Views.UartView>();
        }
    }
}
