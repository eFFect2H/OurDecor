using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    public class ChatMapping
    {
        public string Object { get; set; }
        public string Direction { get; set; }
        public long ChatId { get; set; }
        public string InviteLink { get; internal set; }
        public object Username { get; internal set; }
    }
}
