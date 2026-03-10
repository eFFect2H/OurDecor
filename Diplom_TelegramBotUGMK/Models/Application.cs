using System;
using System.Collections.Generic;

namespace Telegrame_Test.Models
{
    public class Application
    {
        public DateTime? DateTime { get; set; }
        public string Object { get; set; }
        public string Direction { get; set; }
        public string Location { get; set; }         // новое поле: уточнение местонахождения
        public string Urgency { get; set; }          // новое поле: "Срочно" / "Не срочно" или свободный текст
        public string Deadline { get; set; }         // новое поле: строковое представление срока (например "до 15:00" или дата)
        public string Description { get; set; }
        public string ContactName { get; set; }
        public string ContactPhone { get; set; }
        // ✅ ОБЪЕДИНЕНО: фото и видео вместе (список ID из Telegram)
        public List<string> MediaFileIds { get; set; } = new List<string>();

        // ✅ ОБЪЕДИНЕНО: ссылки на фото и видео (списокссылок из Google Drive)
        public List<string> ClosureMediaLinks { get; set; } = new List<string>();
        public string CloseComment { get; set; } // ✅ НОВОЕ: комментарий при закрытии
        public long TelegramUserId { get; set; }
        public string Status { get; set; } = "";
        public int RowId { get; set; }
        public long CreatorTelegramId { get; set; } // ID создателя заявки
        public long? ClosedByTelegramId { get; set; } // ID того, кто закрыл заявку
        public DateTime? ClosedAt { get; set; } // Время закрытия заявки
    }
}
