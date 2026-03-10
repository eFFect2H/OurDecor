using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegrame_Test.Models;

namespace Telegrame_Test.Services
{
    public class DirectionalSheetsService
    {
        private readonly SheetsService _sheetsService;
        private readonly IConfiguration _configuration;
        private readonly Dictionary<string, DirectionSheetConfig> _directionConfigs;

        public class DirectionSheetConfig
        {
            public string SpreadsheetId { get; set; }
            public string SheetName { get; set; }
            public bool Enabled { get; set; }
        }

        public DirectionalSheetsService(
            SheetsService sheetsService,
            IConfiguration configuration)
        {
            _sheetsService = sheetsService;
            _configuration = configuration;
            _directionConfigs = LoadDirectionConfigs();
        }

        
        // Загружает конфигурацию отдельных таблиц для каждого направления (по InternalName)
        private Dictionary<string, DirectionSheetConfig> LoadDirectionConfigs()
        {
            var configs = new Dictionary<string, DirectionSheetConfig>();
            var directionSheetsSection = _configuration.GetSection("DirectionSheets");

            if (directionSheetsSection.Exists())
            {
                foreach (var child in directionSheetsSection.GetChildren())
                {
                    // ⭐ Ключ — это InternalName направления
                    var internalDirectionName = child.Key;
                    Log.Information("DirectionSheet config loaded: Key='{Key}', Length={Length}",internalDirectionName, internalDirectionName?.Length ?? -1);


                    var config = new DirectionSheetConfig
                    {
                        SpreadsheetId = child["SpreadsheetId"],
                        SheetName = child["SheetName"] ?? "Заявки",
                        Enabled = bool.TryParse(child["Enabled"], out var enabled) && enabled
                    };

                    if (!string.IsNullOrEmpty(config.SpreadsheetId) && config.Enabled)
                    {
                        configs[internalDirectionName] = config;
                        Log.Information(
                            "Загружена конфигурация отдельной таблицы для направления: '{Direction}' " +
                            "(SpreadsheetId={SpreadsheetId}, Sheet='{SheetName}')",
                            internalDirectionName, config.SpreadsheetId, config.SheetName);
                    }
                    else if (!string.IsNullOrEmpty(config.SpreadsheetId) && !config.Enabled)
                    {
                        Log.Information(
                            "Направление '{Direction}' отключено в конфигурации",
                            internalDirectionName);
                    }
                }
            }
            else
            {
                Log.Warning("⚠️ Секция 'DirectionSheets' не найдена в конфигурации");
            }

            return configs;
        }

        
        // Проверяет, есть ли отдельная таблица для данного направления (по InternalName)
        public bool HasDirectionSheet(string internalDirectionName)
        {
            var hasSheet = _directionConfigs.ContainsKey(internalDirectionName);

            Log.Debug(
                "Проверка направления '{Direction}':  {Result}",
                internalDirectionName,
                hasSheet ? "✅ найдена в конфиге" : "❌ не найдена");

            return hasSheet;
        }
        
        
        // Получает конфигурацию для направления
        public DirectionSheetConfig GetDirectionConfig(string internalDirectionName)
        {
            return _directionConfigs.TryGetValue(internalDirectionName, out var config) ? config : null;
        }


        // Записывает заявку в отдельную таблицу, если она есть для этого направления (по InternalName)
        public async Task<(bool success, int rowId, string message)> AppendApplicationToDirectionSheetAsync(Application application, string internalDirectionName, ITelegramBotClient botClient)
        {
            if (!_directionConfigs.TryGetValue(internalDirectionName, out var config))
            {
                Log.Debug(
                    "Отдельная таблица для направления '{Direction}' не настроена",
                    internalDirectionName);

                return (false, -1,
                    $"Отдельная таблица для направления '{internalDirectionName}' не настроена");
            }

            try
            {
                Log.Information(
                    "Начинаем запись заявки в отдельную таблицу для направления '{Direction}'",
                    internalDirectionName);

                await EnsureDirectionSheetExistsAsync(config, internalDirectionName);

                var mediaLinks = new List<string>();
                if (application.MediaFileIds?.Any() == true)
                {
                    foreach (var mediaId in application.MediaFileIds)
                    {
                        if (mediaId.StartsWith("http"))
                        {
                            mediaLinks.Add(mediaId);
                        }
                    }
                }

                string mediaUrls = string.Join(", ", mediaLinks);

                string telegramNick = application.TelegramUserId > 0
                    ? await GetTelegramUsernameAsync(botClient, application.TelegramUserId)
                    : "";

                string telegramIdOrNick = !string.IsNullOrEmpty(telegramNick)
                    ? telegramNick
                    : application.TelegramUserId.ToString();

                string urgencyText = application.Urgency ?? "";
                if (!string.IsNullOrWhiteSpace(application.Deadline))
                {
                    urgencyText = $"{urgencyText} ({application.Deadline})";
                }

                var creationDateTime = DateTime.Now;

                // ✅ ИСПРАВЛЕНИЕ: Используем диапазон A2:O вместо A:O
                // Это гарантирует что данные будут добавлены после заголовка
                var range = $"'{config.SheetName}'!A2:O";
                var valueRange = new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object>
                        {
                            creationDateTime.ToString("dd.MM.yyyy HH:mm:ss"),
                            "Открыто",
                            "",
                            application.Object,
                            application.Direction,
                            application.Description,
                            $"{application.ContactName} {application.ContactPhone}",
                            mediaUrls,
                            "",
                            telegramIdOrNick,
                            "",
                            application.CreatorTelegramId.ToString(),
                            "",
                            urgencyText,
                            application.Location ?? ""
                        }
                    }
                };

                var request = _sheetsService.Spreadsheets.Values.Append(
                    valueRange,
                    config.SpreadsheetId,
                    range);
                request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
                var appendResponse = await request.ExecuteAsync();

                // ✅ ИСПРАВЛЕНИЕ: Правильный подсчёт номера строки
                int rowId = -1;
                if (appendResponse.Updates != null && !string.IsNullOrEmpty(appendResponse.Updates.UpdatedRange))
                {
                    var updatedRange = appendResponse.Updates.UpdatedRange;
                    Log.Debug("UpdatedRange: {UpdatedRange}", updatedRange);

                    // Пример: 'Электрика'!A2:O2
                    var parts = updatedRange.Split('!');
                    if (parts.Length == 2)
                    {
                        var cellsRange = parts[1];
                        // Из "A2:O2" извлекаем "2"
                        var match = System.Text.RegularExpressions.Regex.Match(cellsRange, @"(\d+)$");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedRow))
                        {
                            rowId = parsedRow;
                            Log.Debug("Номер строки отдельной таблицы определен из UpdatedRange: {RowId}", rowId);
                        }
                    }
                }

                if (rowId == -1)
                {
                    Log.Warning("Не удалось определить RowId из UpdatedRange, используем fallback");

                    var lastRowRange = $"'{config.SheetName}'!A:A";
                    var lastRowResponse = await _sheetsService.Spreadsheets.Values
                        .Get(config.SpreadsheetId, lastRowRange)
                        .ExecuteAsync();

                    // ✅ ИСПРАВЛЕНИЕ: Правильный подсчёт (количество значений = последняя строка)
                    rowId = lastRowResponse.Values != null ? lastRowResponse.Values.Count : 2;
                    Log.Debug("Номер строки определен через fallback: {RowId}", rowId);
                }

                Log.Information(
                    "Заявка успешно записана в отдельную таблицу для направления '{Direction}' " +
                    "(SpreadsheetId={SpreadsheetId}, RowId={RowId})",
                    internalDirectionName, config.SpreadsheetId, rowId);

                return (true, rowId,
                    $"Записано в таблицу '{config.SheetName}' (направление: {internalDirectionName})");
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "❌ Ошибка при записи заявки в отдельную таблицу для направления '{Direction}'",
                    internalDirectionName);

                return (false, -1, $"Ошибка записи: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверяет и инициализирует лист для направления, если его нет
        /// </summary>
        private async Task EnsureDirectionSheetExistsAsync(DirectionSheetConfig config, string directionName)
        {
            try
            {
                var spreadsheet = await _sheetsService.Spreadsheets.Get(config.SpreadsheetId).ExecuteAsync();
                var sheetExists = spreadsheet.Sheets.Any(s => s.Properties.Title == config.SheetName);

                if (!sheetExists)
                {
                    Log.Information(
                        "Лист '{SheetName}' не найден в таблице {SpreadsheetId}.  Создаём новый.. .",
                        config.SheetName, config.SpreadsheetId);

                    // Создаём новый лист
                    var requestBody = new AddSheetRequest
                    {
                        Properties = new SheetProperties
                        {
                            Title = config.SheetName,
                            GridProperties = new GridProperties { RowCount = 1000, ColumnCount = 15 }
                        }
                    };

                    var batchUpdateRequest = new BatchUpdateSpreadsheetRequest
                    {
                        Requests = new List<Request> { new Request { AddSheet = requestBody } }
                    };

                    await _sheetsService.Spreadsheets.BatchUpdate(batchUpdateRequest, config.SpreadsheetId)
                        .ExecuteAsync();

                    // Добавляем заголовки
                    var range = $"'{config.SheetName}'!A1:O1";
                    var valueRange = new ValueRange
                    {
                        Values = new List<IList<object>>
                        {
                            new List<object>
                            {
                                "Дата и время создания",    // A
                                "Статус",                   // B
                                "Дата и время закрытия",    // C
                                "Объек",                   // D
                                "Направление",              // E
                                "Описание",                 // F
                                "ФИО и Контакты",           // G
                                "Фото заявителя",           // H
                                "Фото подтверждение",       // I
                                "Telegram User заявителя",   // J
                                "Telegram User закрывающего",// K
                                "ID Telegram заявителя",     // L
                                "ID Telegram закрывающего",   // M
                                "Срочность",                  // N
                                "Местоположение"            // O
                            }
                        }
                    };

                    var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, config.SpreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                    await updateRequest.ExecuteAsync();

                    Log.Information(
                        "Создан новый лист '{SheetName}' в таблице {SpreadsheetId}",
                        config.SheetName, config.SpreadsheetId);
                }
                else
                {
                    Log.Debug(
                        "Лист '{SheetName}' уже существует",
                        config.SheetName);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "Не удалось инициализировать лист для направления '{Direction}' (SheetName='{SheetName}')",
                    directionName, config.SheetName);
                // Не кидаем исключение — продолжаем работу
            }
        }

        
        // Обновляет статус в отдельной таблице при закрытии заявки
        public async Task UpdateStatusInDirectionSheetAsync(int rowId, string internalDirectionName, string newStatus, long closedByUserId, ITelegramBotClient botClient)
        {
            if (!_directionConfigs.TryGetValue(internalDirectionName, out var config))
            {
                Log.Debug(
                    "Не найдена конфигурация для обновления статуса направления '{Direction}'",
                    internalDirectionName);
                return;
            }

            try
            {
                var closedByUserName = await GetTelegramUsernameAsync(botClient, closedByUserId);
                var now = DateTime.Now;

                var updates = new[]
                {
                    (Range: $"'{config.SheetName}'!B{rowId}", Value: (object)newStatus),
                    (Range: $"'{config.SheetName}'!C{rowId}",
                        Value: (object)(newStatus == "Закрыто" ? now.ToString("dd.MM.yyyy HH:mm:ss") : "")),
                    (Range: $"'{config.SheetName}'!K{rowId}", Value: (object)closedByUserName),
                    (Range:  $"'{config.SheetName}'!M{rowId}", Value: (object)closedByUserId. ToString())
                };

                foreach (var (range, value) in updates)
                {
                    var valueRange = new ValueRange
                    {
                        Values = new List<IList<object>> { new List<object> { value } }
                    };

                    var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, config.SpreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                    await updateRequest.ExecuteAsync();
                }

                Log.Information(
                    "Статус обновлен в отдельной таблице для направления '{Direction}' (RowId={RowId}, Status={Status})",
                    internalDirectionName, rowId, newStatus);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "Ошибка при обновлении статуса в отдельной таблице для направления '{Direction}' (RowId={RowId})",
                    internalDirectionName, rowId);
            }
        }

        /// <summary>
        /// Обновляет редактируемые поля в отдельной таблице при редактировании заявки
        /// </summary>
        public async Task UpdateEditableFieldsInDirectionSheetAsync(
            Application app,
            string internalDirectionName)
        {
            if (!_directionConfigs.TryGetValue(internalDirectionName, out var config))
            {
                Log.Debug(
                    "Не найдена конфигурация для обновления направления '{Direction}'",
                    internalDirectionName);
                return;
            }

            try
            {
                Log.Information(
                    "Обновляем редактируемые поля в отдельной таблице для направления '{Direction}' (RowId={RowId})",
                    internalDirectionName, app.RowId);

                var updates = new List<(string Range, string Value)>();
                int row = app.RowId;

                // Обновляем только те поля, которые могут быть отредактированы

                if (!string.IsNullOrWhiteSpace(app.Location))
                    updates.Add(($"'{config.SheetName}'!O{row}", app.Location));

                if (!string.IsNullOrWhiteSpace(app.Description))
                    updates.Add(($"'{config.SheetName}'!F{row}", app.Description));

                if (!string.IsNullOrWhiteSpace(app.ContactName) || !string.IsNullOrWhiteSpace(app.ContactPhone))
                    updates.Add(($"'{config.SheetName}'!G{row}", $"{app.ContactName} {app.ContactPhone}".Trim()));

                // Срочность и дедлайн → столбец N
                if (!string.IsNullOrWhiteSpace(app.Urgency) || !string.IsNullOrWhiteSpace(app.Deadline))
                {
                    var urgencyValue = app.Urgency ?? "";
                    if (!string.IsNullOrWhiteSpace(app.Deadline))
                    {
                        urgencyValue = $"{urgencyValue} ({app.Deadline})";
                    }
                    updates.Add(($"'{config.SheetName}'!N{row}", urgencyValue));
                }

                // Выполняем все обновления
                foreach (var (range, value) in updates)
                {
                    var valueRange = new ValueRange
                    {
                        Values = new List<IList<object>>
                        {
                            new List<object> { value }
                        }
                    };

                    var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, config.SpreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                    await updateRequest.ExecuteAsync();

                    Log.Debug("Обновлено: {Range} = {Value}", range, value);
                }

                Log.Information(
                    "Редактируемые поля успешно обновлены в отдельной таблице для направления '{Direction}' (RowId={RowId})",
                    internalDirectionName, app.RowId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "Ошибка при обновлении редактируемых полей в отдельной таблице для направления '{Direction}' (RowId={RowId})",
                    internalDirectionName, app.RowId);
            }
        }

        private async Task<string> GetTelegramUsernameAsync(ITelegramBotClient botClient, long userId)
        {
            try
            {
                var chat = await botClient.GetChatAsync(userId);
                return !string.IsNullOrEmpty(chat.Username) ? "@" + chat.Username : "";
            }
            catch
            {
                return "";
            }
        }

        
        // Получает список всех доступных направлений с отдельными таблицами
        public List<string> GetAvailableDirections()
        {
            return _directionConfigs.Keys.ToList();
        }
    }
}