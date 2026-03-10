using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    [Index(nameof(TelegramId), IsUnique = true)]
    public class User
    {
        public long TelegramId { get; set; }
        public bool IsAdmin { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? InvitationId { get; set; }           // FK на приглашение, через которое был добавлен
        public Invitation Invitation { get; set; }       // Навигационное свойство
    }
}
