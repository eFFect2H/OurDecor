using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegrame_Test.Models;
using Telegrame_Test.Service;

namespace Telegrame_Test.Services
{
    public class DailyReminderService
    {
        private readonly ITelegramSendClient _botClient;
        private readonly GoogleSheetsService _sheetsService;
        private readonly IConfiguration _configuration;
        private readonly ApplicationDbContext _dbContext;  

        public DailyReminderService(ITelegramSendClient botClient, GoogleSheetsService sheetsService, IConfiguration configuration, ApplicationDbContext dbContext)  
        {
            _botClient = botClient;
            _sheetsService = sheetsService;
            _configuration = configuration;
            _dbContext = dbContext;  
        }

        enum NotifyReason
        {
            None,
            HighUrgency,
            MediumDaily,
            LowBeforeDeadline,
            LowDeadlineToday,
            LowOverdue
        }

        public enum UrgencyLevel
        {
            High,
            Medium,
            Low,
            Unknown
        }


        // ============== Уведомления о заявках =========================
        public async Task SendOpenApplicationsRemindersAsync()
        {
            var chatMappings = _configuration
                .GetSection("ChatMappings")
                .Get<List<ChatMapping>>() ?? new();

            var openApps = await _sheetsService.GetOpenApplicationsAsync();
            var now = DateTime.Now;

            var appsToNotify = openApps
                .Where(app => ShouldNotify(app, now, out _))
                .ToList();

            var grouped = appsToNotify
                .GroupBy(a => new { a.Object, a.Direction });

            foreach (var group in grouped)
            {
                var mapping = chatMappings.FirstOrDefault(c =>
                    c.Object.Equals(group.Key.Object, StringComparison.OrdinalIgnoreCase) &&
                    c.Direction.Equals(group.Key.Direction, StringComparison.OrdinalIgnoreCase));

                if (mapping == null)
                    continue;

                var message = BuildMessage(group.Key.Object, group.Key.Direction, group);

                try
                {
                    await _botClient.Client.SendTextMessageAsync(mapping.ChatId, message, parseMode: ParseMode.Markdown);

                    // ✅ ЛОГИРУЕМ ОТПРАВКУ ДЛЯ КАЖДОЙ ЗАЯВКИ
                    foreach (var app in group)
                    {
                        await MarkNotifiedInDatabaseAsync(app.RowId, now);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка отправки уведомления в чат {ChatId}", mapping.ChatId);
                }
            }
        }

        private bool ShouldNotify(Application app, DateTime now, out NotifyReason reason)
        {
            reason = NotifyReason.None;

            var urgency = ParseUrgency(app.Urgency);
            var deadline = ParseDeadline(app.Urgency);

            // ❌ заявка без даты создания — не уведомляем
            if (!app.DateTime.HasValue)
                return false;

            // ❌ защита от мгновенных уведомлений
            if (urgency != UrgencyLevel.High &&
                (now - app.DateTime.Value) < TimeSpan.FromHours(5))
                return false;

            // 🔁 ограничение частоты — ПРОВЕРЯЕМ БД ВМЕСТО ПАМЯТИ
            var lastNotified = GetLastNotificationTimeFromDatabase(app.RowId);
            if (lastNotified.HasValue)
            {
                var elapsed = now - lastNotified.Value;

                if (urgency == UrgencyLevel.High && elapsed < TimeSpan.FromHours(3))
                    return false;

                if (urgency is UrgencyLevel.Medium or UrgencyLevel.Low &&
                    elapsed < TimeSpan.FromHours(24))
                    return false;
            }

            // 🔴 ВЫСОКИЙ — всегда
            if (urgency == UrgencyLevel.High)
            {
                reason = NotifyReason.HighUrgency;
                return true;
            }

            // 🟡 СРЕДНИЙ — 1 раз в день
            if (urgency == UrgencyLevel.Medium)
            {
                reason = NotifyReason.MediumDaily;
                return true;
            }

            // 🟢 НИЗКИЙ — дедлайн
            if (urgency == UrgencyLevel.Low && deadline.HasValue)
            {
                var days = (deadline.Value.Date - now.Date).Days;

                if (days > 1)
                    return false;

                if (days == 1)
                {
                    reason = NotifyReason.LowBeforeDeadline;
                    return true;
                }

                if (days == 0 && now.Hour < 11)
                {
                    reason = NotifyReason.LowDeadlineToday;
                    return true;
                }

                // ❗ ПРОСРОЧЕНО — уведомляем 1 раз в сутки
                if (days < 0)
                {
                    reason = NotifyReason.LowOverdue;
                    return true;
                }
            }

            return false;
        }

        private DateTime? GetLastNotificationTimeFromDatabase(int applicationRowId)
        {
            try
            {
                var log = _dbContext.ReminderNotificationLogs
                    .FirstOrDefault(x => x.ApplicationRowId == applicationRowId);

                return log?.LastNotifiedAt;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при получении времени последнего уведомления для заявки {RowId}", applicationRowId);
                return null;
            }
        }

        /// <summary>
        /// Сохраняет время уведомления в БД
        private async Task MarkNotifiedInDatabaseAsync(int applicationRowId, DateTime notifiedAt)
        {
            try
            {
                var existing = await _dbContext.ReminderNotificationLogs
                    .FirstOrDefaultAsync(x => x.ApplicationRowId == applicationRowId);

                if (existing != null)
                {
                    existing.LastNotifiedAt = notifiedAt;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _dbContext.ReminderNotificationLogs.Add(new ReminderNotificationLog
                    {
                        ApplicationRowId = applicationRowId,
                        LastNotifiedAt = notifiedAt,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await _dbContext.SaveChangesAsync();
                Log.Debug("Запись уведомления для заявки {RowId} сохранена в БД", applicationRowId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при сохранении времени уведомления для заявки {RowId}", applicationRowId);
            }
        }

        private static UrgencyLevel ParseUrgency(string? urgency)
        {
            if (string.IsNullOrWhiteSpace(urgency))
                return UrgencyLevel.Unknown;

            urgency = urgency.Trim();

            if (urgency.StartsWith("🔴"))
                return UrgencyLevel.High;

            if (urgency.StartsWith("\U0001f7e1"))
                return UrgencyLevel.Medium;

            if (urgency.StartsWith("\U0001f7e2"))
                return UrgencyLevel.Low;

            return UrgencyLevel.Unknown;
        }

        private static DateTime? ParseDeadline(string? urgency)
        {
            if (string.IsNullOrWhiteSpace(urgency))
                return null;

            // Ищем дату формата dd.MM.yyyy В ЛЮБОМ МЕСТЕ строки
            var match = System.Text.RegularExpressions.Regex.Match(
                urgency,
                @"\b\d{2}\.\d{2}\.\d{4}\b"
            );

            if (!match.Success)
                return null;

            if (DateTime.TryParseExact(
                match.Value,
                "dd.MM.yyyy",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date))
            {
                return date;
            }

            return null;
        }

        private static string BuildMessage(string obj, string direction, IEnumerable<Application> apps)
        {
            var list = apps.ToList();

            var sb = new StringBuilder();

            sb.AppendLine($"🔔 Напоминание об открытых заявках ({obj} / {direction})");
            sb.AppendLine($"📝 Всего открытых заявок: {apps.Count()}");
            sb.AppendLine("──────────────");

            foreach (var a in list)
            {
                sb.AppendLine($"🆔 *#{a.RowId}* | {a.Description}");
                sb.AppendLine($"{a.Urgency}");

                if (!string.IsNullOrWhiteSpace(a.ContactName) || !string.IsNullOrWhiteSpace(a.ContactPhone))
                {
                    sb.AppendLine($"👤 *Контакт:* {a.ContactName} {a.ContactPhone}".Trim());
                }
                sb.AppendLine("──────────────");
            }

            return sb.ToString();
        }



        // ===================== Генерация отчетов ========================================
        private static List<ApplicationSummaryRow> BuildSummary(IEnumerable<Application> applications, DateTime from, DateTime to)
        {
            return applications
                .Where(a =>
                    a.DateTime.HasValue &&
                    a.DateTime.Value >= from &&
                    a.DateTime.Value <= to)
                .GroupBy(a => new { a.Object, a.Direction })
                .Select(g =>
                {
                    var total = g.Count();
                    var open = g.Count(x => x.Status == "Открыто");
                    var closed = g.Count(x => x.Status == "Закрыто");

                    var avgCloseHours = g
                        .Where(x => x.ClosedAt.HasValue && x.DateTime.HasValue)
                        .Select(x => (x.ClosedAt.Value - x.DateTime.Value).TotalHours)
                        .DefaultIfEmpty()
                        .Average();

                    var oldestOpenDays = g
                        .Where(x => x.Status == "Открыто" && x.DateTime.HasValue)
                        .Select(x => (DateTime.Now - x.DateTime.Value).Days)
                        .DefaultIfEmpty()
                        .Max();

                    return new ApplicationSummaryRow
                    {
                        Object = g.Key.Object,
                        Direction = g.Key.Direction,
                        Total = total,
                        Open = open,
                        Closed = closed,
                        AvgCloseHours = closed > 0 ? Math.Round(avgCloseHours, 1) : null,
                        OldestOpenDays = oldestOpenDays
                    };
                })
                .OrderBy(x => x.Object)
                .ThenBy(x => x.Direction)
                .ToList();
        }

        private static List<(string Object, string Direction, int Total, int Open, int Closed)>BuildSummary(IEnumerable<Application> applications)
        {
            return applications
                .GroupBy(a => new { a.Object, a.Direction })
                .Select(g => (
                    Object: g.Key.Object,
                    Direction: g.Key.Direction,
                    Total: g.Count(),
                    Open: g.Count(x => x.Status == "Открыто"),
                    Closed: g.Count(x => x.Status == "Закрыто")
                ))
                .OrderBy(x => x.Object)
                .ThenBy(x => x.Direction)
                .ToList();
        }


        public async Task SendWeeklyReportAsync(long adminChatId)
        {
            
            // 1. Период отчёта — последние 7 дней
            var periodEnd = DateTime.Now;
            var periodStart = periodEnd.AddDays(-7).Date;

            // 2. Агрегация
            var applications = await _sheetsService.GetApplicationsAsync(periodStart, periodEnd);
            var summary = BuildSummary(applications, periodStart, periodEnd);

            using var workbook = new XLWorkbook();

            // ===== ЛИСТ 1: СВОДКА =====
            var worksheet = workbook.Worksheets.Add("Summary");

            worksheet.Cell(1, 1).Value =
                $"Отчет за период: {periodStart:dd.MM.yyyy} – {periodEnd:dd.MM.yyyy}";
            worksheet.Cell(1, 1).Style.Font.Bold = true;
            worksheet.Range(1, 1, 1, 7).Merge();

            worksheet.Cell(2, 1).Value = "Объект";
            worksheet.Cell(2, 2).Value = "Направление";
            worksheet.Cell(2, 3).Value = "Всего";
            worksheet.Cell(2, 4).Value = "Открыто";
            worksheet.Cell(2, 5).Value = "Закрыто";
            worksheet.Cell(2, 6).Value = "Среднее время закрытия (ч)";
            worksheet.Cell(2, 7).Value = "Самая старая открытая (дн.)";

            worksheet.Range(2, 1, 2, 7).Style.Font.Bold = true;

            int row = 3;

            foreach (var item in summary)
            {
                worksheet.Cell(row, 1).Value = item.Object;
                worksheet.Cell(row, 2).Value = item.Direction;
                worksheet.Cell(row, 3).Value = item.Total;
                worksheet.Cell(row, 4).Value = item.Open;
                worksheet.Cell(row, 5).Value = item.Closed;
                worksheet.Cell(row, 6).Value = item.AvgCloseHours;
                worksheet.Cell(row, 7).Value = item.OldestOpenDays;
                row++;
            }

            // Итоги
            worksheet.Cell(row, 1).Value = "ИТОГО";
            worksheet.Cell(row, 1).Style.Font.Bold = true;
            worksheet.Cell(row, 3).Value = summary.Sum(x => x.Total);
            worksheet.Cell(row, 4).Value = summary.Sum(x => x.Open);
            worksheet.Cell(row, 5).Value = summary.Sum(x => x.Closed);

            worksheet.Columns().AdjustToContents();

            // ===== ЛИСТ 2: ДЕТАЛИ =====
            var detailsSheet = workbook.Worksheets.Add("Details");

            detailsSheet.Cell(1, 1).Value = "Дата";
            detailsSheet.Cell(1, 2).Value = "Объект";
            detailsSheet.Cell(1, 3).Value = "Направление";
            detailsSheet.Cell(1, 4).Value = "Статус";
            detailsSheet.Cell(1, 5).Value = "Приоритет";
            detailsSheet.Cell(1, 6).Value = "Местонахождение";
            detailsSheet.Cell(1, 7).Value = "Описание";

            detailsSheet.Range(1, 1, 1, 7).Style.Font.Bold = true;

            int detailRow = 2;

            foreach (var app in applications
                .Where(a => a.DateTime >= periodStart && a.DateTime <= periodEnd)
                .OrderBy(a => a.DateTime))
            {
                detailsSheet.Cell(detailRow, 1).Value = app.DateTime;
                detailsSheet.Cell(detailRow, 2).Value = app.Object;
                detailsSheet.Cell(detailRow, 3).Value = app.Direction;
                detailsSheet.Cell(detailRow, 4).Value = app.Status;
                detailsSheet.Cell(detailRow, 5).Value = app.Urgency;
                detailsSheet.Cell(detailRow, 6).Value = app.Location;
                detailsSheet.Cell(detailRow, 7).Value = app.Description;
                detailRow++;
            }

            detailsSheet.Columns().AdjustToContents();

            // ===== ОТПРАВКА =====
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            stream.Position = 0;

            await _botClient.Client.SendDocumentAsync(
                adminChatId,
                InputFile.FromStream(stream, "weekly_report.xlsx"),
                caption: "📊 Еженедельный отчет");
        }

        // ----
        public async Task SendAllReportAsync(long adminChatId)
        {
            // 1. Получаем ВСЕ заявки (без фильтра по дате)
            var applications = await _sheetsService.GetApplicationsAsync(
                DateTime.MinValue,
                DateTime.MaxValue);

            if (applications.Count == 0)
            {
                await _botClient.Client.SendTextMessageAsync(
                    adminChatId,
                    "📭 Нет данных для формирования отчёта.");
                return;
            }

            // 2. Группируем через единый агрегатор
            var summary = BuildSummary(applications);

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("AllApplicationsReport");

            // ===== Заголовок =====
            worksheet.Cell(1, 1).Value = "📊 Общий отчёт по всем заявкам";
            worksheet.Cell(1, 1).Style.Font.Bold = true;
            worksheet.Cell(1, 1).Style.Font.FontSize = 14;
            worksheet.Range(1, 1, 1, 6).Merge();

            worksheet.Cell(2, 1).Value = $"Сформирован: {DateTime.Now:dd.MM.yyyy HH:mm}";
            worksheet.Range(2, 1, 2, 6).Merge();

            // ===== Заголовки таблицы =====
            worksheet.Cell(4, 1).Value = "Объект";
            worksheet.Cell(4, 2).Value = "Направление";
            worksheet.Cell(4, 3).Value = "Всего";
            worksheet.Cell(4, 4).Value = "Открыто";
            worksheet.Cell(4, 5).Value = "Закрыто";
            worksheet.Cell(4, 6).Value = "% Закрытых";

            worksheet.Range(4, 1, 4, 6).Style.Font.Bold = true;

            // ===== Данные =====
            int row = 5;

            int totalSum = 0;
            int openSum = 0;
            int closedSum = 0;

            foreach (var item in summary)
            {
                worksheet.Cell(row, 1).Value = item.Object;
                worksheet.Cell(row, 2).Value = item.Direction;
                worksheet.Cell(row, 3).Value = item.Total;
                worksheet.Cell(row, 4).Value = item.Open;
                worksheet.Cell(row, 5).Value = item.Closed;

                worksheet.Cell(row, 6).Value =
                    item.Total > 0
                        ? Math.Round((double)item.Closed / item.Total * 100, 1)
                        : 0;

                totalSum += item.Total;
                openSum += item.Open;
                closedSum += item.Closed;

                row++;
            }

            // ===== Итоги =====
            worksheet.Cell(row, 1).Value = "ИТОГО";
            worksheet.Cell(row, 1).Style.Font.Bold = true;

            worksheet.Cell(row, 3).Value = totalSum;
            worksheet.Cell(row, 4).Value = openSum;
            worksheet.Cell(row, 5).Value = closedSum;
            worksheet.Cell(row, 6).Value =
                totalSum > 0
                    ? Math.Round((double)closedSum / totalSum * 100, 1)
                    : 0;

            worksheet.Range(row, 1, row, 6).Style.Font.Bold = true;

            // ===== Форматирование =====
            worksheet.Columns().AdjustToContents();
            worksheet.SheetView.FreezeRows(4);

            // ===== Отправка =====
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            stream.Position = 0;

            await _botClient.Client.SendDocumentAsync(
                adminChatId,
                InputFile.FromStream(stream, "all_applications_report.xlsx"),
                caption: "📊 Общий отчёт по всем заявкам");
        }



    }

}
