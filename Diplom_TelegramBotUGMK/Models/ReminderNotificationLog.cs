using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    public class ReminderNotificationLog
    {
        public int Id { get; set; }
        public int ApplicationRowId { get; set; }  // ID заявки из Google Sheets
        public DateTime LastNotifiedAt { get; set; }  // Когда последний раз отправили уведомление
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
