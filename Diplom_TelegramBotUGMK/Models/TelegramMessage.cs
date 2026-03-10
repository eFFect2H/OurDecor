using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    public class TelegramMessage
    {
        public int Id { get; set; }
        public string SheetName { get; set; }
        public int RowId { get; set; }           // номер строки в Google Sheets
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
