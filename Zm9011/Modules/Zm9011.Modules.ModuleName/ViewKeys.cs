using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Zm9011.Modules.ModuleName
{
    /// <summary>模块内使用的导航视图名称常量。</summary>
    public static class ViewKeys
    {
        /// <summary>通用指令页。</summary>
        public const string General = "GeneralView";
        /// <summary>网络服务页。</summary>
        public const string Network = "NetworkView";
        /// <summary>数据相关页。</summary>
        public const string Data = "DataView";
        /// <summary>短信相关页。</summary>
        public const string Sms = "SmsView";
        /// <summary>实时短信页。</summary>
        public const string RealtimeSms = "RealtimeSmsView";
        /// <summary>FOTA 页。</summary>
        public const string Fota = "FotaView";
        /// <summary>ZTE 扩展指令页。</summary>
        public const string ZteExt = "ZteExtView";
        /// <summary>TCP/IP 协议功能页。</summary>
        public const string TcpIp = "TcpIpView";
        /// <summary>UART 串口页。</summary>
        public const string Uart = "UartView";
    }
}
