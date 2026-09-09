using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BarrageGrab.Modles
{
    public class AnchorFinishInfo
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// 抖音号
        /// </summary>
        public string DisplayId { get; set; }

        /// <summary>
        /// SecUid
        /// </summary>
        public string SecUid { get; set; }

        /// <summary>
        /// 昵称
        /// </summary>
        public string Nickname { get; set; }

        /// <summary>
        /// 头像地址
        /// </summary>
        public string HeadUrl { get; set; }
        
        /// <summary>
        /// 直播间号
        /// </summary>
        public string RoomId { get; set; }
    }
}
