using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    public class Invitation
    {
        public int Id { get; set; }                      // Первичный ключ
        public string Token { get; set; }                // Уникальный токен
        public long InvitedBy { get; set; }              // Telegram ID администратора
        public long? InvitedUserId { get; set; }         // Telegram ID приглашенного пользователя (null до использования)
        public DateTime CreatedAt { get; set; }          // Когда создано приглашение
        public DateTime ExpiresAt { get; set; }          // Когда истекает (24 часа)
        public bool IsUsed { get; set; }                 // Использовано ли приглашение
        public DateTime? UsedAt { get; set; }            // Когда приглашение было использовано
    }
}
