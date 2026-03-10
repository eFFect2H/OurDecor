using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    public enum DialogState
    {
        None,
        WaitingForObject,
        WaitingForDirectionCategory,  // ← НОВОЕ состояние для выбора категории
        WaitingForDirection,
        WaitingForLocation,
        WaitingForUrgency,
        WaitingForDeadline,
        WaitingForTime,
        WaitingForDescription,
        WaitingForPhoto,
        WaitingForContact,
        WaitingForClosePhoto,
        WaitingForCloseContact,
        WaitingForCloseRowId,
        WaitingForCloseComment,


        WaitingForEditSelection,
        ChoosingEditField,
        EditingField,
        EditingObjectKeyboard,
        EditingDirectionKeyboard,
        EditingUrgencyKeyboard,
        EditingDeadlineKeyboard,
        EditingUrgencyPick,
        EditingDeadlinePick,
        EditingTimePick,

        WaitingForRedirectRowId,      // Ожидание ввода ID заявки
        WaitingForRedirectChat         // Ожидание выбора целевого чата
    }

    public class UserState
    {
        public DialogState State { get; set; }
        public DialogState PreviousState { get; set; }
        public Application Application { get; set; } = new Application();
        // ✅ ОБЪЕДИНЕНО: медиафайлы (фото и видео вместе)
        public List<string> MediaFileIds { get; set; } = new List<string>();
        public List<string> ClosureMediaFileIds { get; set; } = new List<string>();
        public DateTime? CalendarMonth { get; set; }
        public int? RowId { get; set; }
        public string ExecutorFullName { get; set; } // ФИО исполнителя
        public string DispatcherFullName { get; set; } // ФИО диспетчера
        public string? CloseComment { get; set; } // комментарий исполнителя
        public long? OriginalCreatorId { get; set; }

        public string EditingField { get; set; }
        public string SelectedDirectionCategory { get; set; } // ← НОВОЕ для хранения выбранной категории
        public DateTime? LastActivity { get; set; }  // ← НОВОЕ время последней активности
        public DialogState? LastShownState { get; set; }

        // Новые свойства для перенаправления
        public int RedirectRowId { get; set; }
        public Application RedirectApplication { get; set; }
        public List<ChatInfo> AvailableChatsForRedirect { get; set; } = new();
    }
}
