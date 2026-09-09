using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BarrageGrab.Proxy.ProxyEventArgs
{
    public class LiveCompanEventArgs:EventArgs
    {
        /// <summary>
        /// 0登录/进入直播伴侣 ,1开播 ,2关播 ,3登出/退出直播伴侣
        /// </summary>
        public int Action { get; set; }

        /// <summary>
        /// 相关数据
        /// </summary>
        public object Data { get; set; }

        /// <summary>
        /// 进程ID
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// 进程名称
        /// </summary>
        public string ProcessName { get; set; }
    }
}
