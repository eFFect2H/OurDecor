using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegrame_Test.Models;
using static Google.Apis.Requests.BatchRequest;

namespace Telegrame_Test.Service
{
    public class GoogleSheetsService
    {
        private readonly SheetsService _sheetsService;
        private readonly DriveService _driveService;
        private readonly string _spreadsheetId;
        private string _driveFolderId;
        private const string SheetName = "Заметки";

        public GoogleSheetsService(
            IConfiguration configuration,
            SheetsService sheetsService,
            DriveService driveService)
        {
            _spreadsheetId = configuration["Google:Sheets:SpreadsheetId"];
            _driveFolderId = configuration["Google:DriveFolderId"];

            _sheetsService = sheetsService;
            _driveService = driveService;
        }

        public async Task InitializeAsync()
        {
            await EnsureSheetExists();
        }

        private async Task EnsureSheetExists()
        {
            try
            {
                var spreadsheet = await _sheetsService.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
                var sheetExists = spreadsheet.Sheets.Any(s => s.Properties.Title == SheetName);

                if (!sheetExists)
                {
                    var requestBody = new AddSheetRequest
                    {
                        Properties = new SheetProperties
                        {
                            Title = SheetName,
                            GridProperties = new GridProperties { RowCount = 100, ColumnCount = 15 }
                        }
                    };

                    var batchUpdateRequest = new BatchUpdateSpreadsheetRequest
                    {
                        Requests = new List<Request> { new Request { AddSheet = requestBody } }
                    };

                    await _sheetsService.Spreadsheets.BatchUpdate(batchUpdateRequest, _spreadsheetId).ExecuteAsync();

                    spreadsheet = await _sheetsService.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();

                    var range = $"{SheetName}!A1:O1";
                    var valueRange = new ValueRange
                    {
                        Values = new List<IList<object>>
                        {
                            new List<object>
                            {
                                "Дата и время создания",    // A
                                "Статус",                   // B
                                "Дата и время закрытия",    // C
                                "Объект",                   // D
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
                                "Местоположение"
                            }
                        }
                    };

                    var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, _spreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                    await updateRequest.ExecuteAsync();

                    var sheetId = spreadsheet.Sheets.First(s => s.Properties.Title == SheetName).Properties.SheetId.Value;

                    // Добавляем правило валидации для столбца статуса
                    var dataValidationRule = new DataValidationRule
                    {
                        Condition = new BooleanCondition
                        {
                            Type = "ONE_OF_LIST",
                            Values = new List<ConditionValue>
                    {
                        new ConditionValue { UserEnteredValue = "Открыто" },
                        new ConditionValue { UserEnteredValue = "Закрыто" }
                    }
                        },
                        InputMessage = "Выберите статус",
                        Strict = true,
                        ShowCustomUi = true
                    };

                    var setDataValidationRequest = new Request
                    {
                        SetDataValidation = new SetDataValidationRequest
                        {
                            Range = new GridRange
                            {
                                SheetId = sheetId,
                                StartRowIndex = 1,
                                EndRowIndex = 101,
                                StartColumnIndex = 1, // Столбец B (Статус)
                                EndColumnIndex = 2
                            },
                            Rule = dataValidationRule
                        }
                    };

                    var batchUpdateRequestWithValidation = new BatchUpdateSpreadsheetRequest
                    {
                        Requests = new List<Request> { setDataValidationRequest }
                    };

                    await _sheetsService.Spreadsheets.BatchUpdate(batchUpdateRequestWithValidation, _spreadsheetId).ExecuteAsync();
                    Log.Information("Лист {SheetName} успешно создан с заголовками и дропдауном для статуса.", SheetName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при создании листа или настройке дропдауна {SheetName}", SheetName);
                throw;
            }
        }

        public async Task<(int rowId, string photoUrl)> AppendApplicationAsync(Application application, ITelegramBotClient botClient, long chatId)
        {
            try
            {
                // Установим текущую дату и время создания
                var creationDateTime = DateTime.Now;

                var mediaLinks = new List<string>();
                foreach (var id in application.MediaFileIds ?? Enumerable.Empty<string>())
                {
                    var link = await UploadMediaToDriveAsync(id, botClient);
                    mediaLinks.Add(!string.IsNullOrEmpty(link) ? link : id);
                }

                string mediaUrls = string.Join(", ", mediaLinks);

                // Получаем никнейм пользователя через Telegram API
                string telegramNick = application.TelegramUserId > 0
                    ? await GetTelegramUsernameAsync(botClient, application.TelegramUserId)
                    : "";

                string telegramIdOrNick = !string.IsNullOrEmpty(telegramNick)
                    ? telegramNick
                    : application.TelegramUserId.ToString();

                // Формируем текст срочности + дедлайна
                string urgencyText = application.Urgency ?? "";
                if (!string.IsNullOrWhiteSpace(application.Deadline))
                {
                    urgencyText = $"{urgencyText} ({application.Deadline})";
                }


                var range = $"{SheetName}!A:O";
                var valueRange = new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object>
                        {
                            creationDateTime.ToString("dd.MM.yyyy HH:mm:ss"),  // A - Дата и время создания
                            "Открыто",                                         // B - Статус
                            "",                                               // C - Дата и время закрытия
                            application.Object,                               // D - Объект
                            application.Direction,                            // E - Направление
                            application.Description,                          // F - Описание
                            $"{application.ContactName} {application.ContactPhone}", // G - ФИО и Контакты
                            mediaUrls,                                       // H - Фото заявителя
                            "",                                              // I - Фото подтверждение
                            telegramIdOrNick,                               // J - Telegram User заявителя
                            "",                                              // K - Telegram User закрывающего
                            application.CreatorTelegramId.ToString(),        // L - ID Telegram заявителя
                            "",                                               // M - ID Telegram закрывающего
                            urgencyText,
                            application.Location ?? ""
                        }
                    }
                };

                var request = _sheetsService.Spreadsheets.Values.Append(valueRange, _spreadsheetId, range);
                request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
                var appendResponse = await request.ExecuteAsync();

                Log.Information("Заявка успешно записана в Google Sheets: {Object} | {Direction}", application.Object, application.Direction);

                // Определяем номер строки
                int rowId = -1;
                if (appendResponse.Updates != null && !string.IsNullOrEmpty(appendResponse.Updates.UpdatedRange))
                {
                    var updatedRange = appendResponse.Updates.UpdatedRange;
                    var parts = updatedRange.Split('!');
                    if (parts.Length == 2)
                    {
                        var cellsRange = parts[1];
                        var cells = cellsRange.Split(':');
                        if (cells.Length == 2)
                        {
                            var lastCell = cells.Last();
                            var rowStr = new string(lastCell.Where(char.IsDigit).ToArray());
                            if (int.TryParse(rowStr, out var parsedRow) && parsedRow > 1)
                            {
                                rowId = parsedRow;
                                Log.Information("Номер строки определен из UpdatedRange: {RowId}", rowId);
                            }
                        }
                    }
                }

                if (rowId == -1)
                {
                    var lastRowRange = $"{SheetName}!A:A";
                    var lastRowResponse = await _sheetsService.Spreadsheets.Values.Get(_spreadsheetId, lastRowRange).ExecuteAsync();
                    rowId = lastRowResponse.Values != null ? lastRowResponse.Values.Count : 2;
                    Log.Information("Номер строки определен через fallback: {RowId}", rowId);
                }

                return (rowId, mediaLinks.FirstOrDefault());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при записи в Google Sheets");
                throw;
            }
        }

        // Добавьте этот вспомогательный метод:
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


        public async Task AddClosurePhotosAsync(int rowId, List<string> photoLinks)
        {
            try
            {
                var photos = string.Join(", ", photoLinks);
                var valueRange = new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object> { photos }
                    }
                };

                var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, _spreadsheetId, $"{SheetName}!I{rowId}");
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                await updateRequest.ExecuteAsync();

                Log.Information("Фото подтверждения для строки {RowId} добавлены: {Photos}", rowId, photos);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при добавлении фото подтверждения для строки {RowId}", rowId);
                throw;
            }
        }

        public async Task<List<Application>> GetOpenApplicationsAsync(CancellationToken ct = default)
        {
            try
            {
                // 1) Получаем только колонку статусов (B)
                var statusRange = $"{SheetName}!B:B";
                var statusRequest = _sheetsService.Spreadsheets.Values.Get(_spreadsheetId, statusRange);
                var statusResponse = await statusRequest.ExecuteAsync(ct).ConfigureAwait(false);

                var openRowNumbers = new List<int>();

                if (statusResponse.Values != null && statusResponse.Values.Count > 0)
                {
                    // values[0] = header (если есть). Индекс 0 -> строка 1 в таблице
                    for (int i = 1; i < statusResponse.Values.Count; i++) // начинаем с 1 чтобы пропустить заголовок
                    {
                        ct.ThrowIfCancellationRequested();
                        var cell = statusResponse.Values[i];
                        var status = (cell != null && cell.Count > 0) ? cell[0]?.ToString().Trim() ?? "" : "";
                        if (string.Equals(status, "Открыто", StringComparison.OrdinalIgnoreCase))
                        {
                            openRowNumbers.Add(i + 1); // i+1 => реальный номер строки в Google Sheets
                        }
                    }
                }
                else
                {
                    Log.Information("Нет данных в колонке статусов.");
                    return new List<Application>();
                }

                if (!openRowNumbers.Any())
                {
                    Log.Information("Найдено открытых заявок: 0");
                    return new List<Application>();
                }

                // 2) Подготовим диапазоны для batchGet: A{row}:N{row}
                var ranges = openRowNumbers.Select(r => $"{SheetName}!A{r}:N{r}").ToList();

                var applications = new List<Application>();
                const int batchSize = 100; // количество диапазонов за один BatchGet 
                for (int offset = 0; offset < ranges.Count; offset += batchSize)
                {
                    Log.Information("Получено строк из Google Sheets: {Count}", statusResponse.Values.Count);
                    ct.ThrowIfCancellationRequested();
                    var batch = ranges.Skip(offset).Take(batchSize).ToList();

                    var batchRequest = _sheetsService.Spreadsheets.Values.BatchGet(_spreadsheetId);
                    batchRequest.Ranges = batch;
                    var batchResponse = await batchRequest.ExecuteAsync(ct).ConfigureAwait(false);

                    // BatchGet.ValueRanges — в том же порядке, что и ranges
                    for (int i = 0; i < batchResponse.ValueRanges.Count; i++)
                    {
                        var vr = batchResponse.ValueRanges[i];
                        var sheetRange = vr.Range; // например "Sheet1!A12:N12"
                        var rowNumber = openRowNumbers[offset + i];

                        var rowVals = vr.Values != null && vr.Values.Count > 0 ? vr.Values[0] : null;
                        if (rowVals == null)
                            continue;

                        // Примем привычные позиции столбцов: A=0 DateTime, B=1 Status, C=2 ClosedAt, D=3 Object, E=4 Direction, F=5 Description, G=6 Contact, H=7 MediaIds, I=8 ClosureLinks, J=9 TelegramUser, K=10 ?, L=11 CreatorId, M=12 ClosedBy, N=13 Urgency
                        DateTime? dateTime = null;
                        if (rowVals.Count > 0 && DateTime.TryParse(rowVals[0]?.ToString(), out var dt)) dateTime = dt;

                        var status = rowVals.Count > 1 ? rowVals[1]?.ToString() ?? "" : "";
                        var closedAt = rowVals.Count > 2 && DateTime.TryParse(rowVals[2]?.ToString(), out var closedDt) ? (DateTime?)closedDt : null;
                        var obj = rowVals.Count > 3 ? rowVals[3]?.ToString() ?? "" : "";
                        var direction = rowVals.Count > 4 ? rowVals[4]?.ToString() ?? "" : "";
                        var description = rowVals.Count > 5 ? rowVals[5]?.ToString() ?? "" : "";

                        var contactRaw = rowVals.Count > 6 ? rowVals[6]?.ToString() ?? "" : "";
                        var contactParts = contactRaw.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        var contactName = contactParts.Length > 0 ? contactParts[0] : "";
                        var contactPhone = contactParts.Length > 1 ? contactParts[1] : "";

                        var mediaIds = rowVals.Count > 7 ? (rowVals[7]?.ToString() ?? "") : "";
                        var mediaList = string.IsNullOrWhiteSpace(mediaIds)
                            ? new List<string>()
                            : mediaIds.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

                        var creatorId = 0L;
                        if (rowVals.Count > 11 && long.TryParse(rowVals[11]?.ToString(), out var parsedCreator))
                            creatorId = parsedCreator;

                        var urgency = rowVals.Count > 13 ? rowVals[13]?.ToString() ?? "" : "";

                        applications.Add(new Application
                        {
                            RowId = rowNumber,
                            DateTime = dateTime ?? DateTime.Now,
                            Status = status,
                            ClosedAt = closedAt,
                            Object = obj,
                            Direction = direction,
                            Description = description,
                            ContactName = contactName,
                            ContactPhone = contactPhone,
                            MediaFileIds = mediaList,
                            CreatorTelegramId = creatorId,
                            TelegramUserId = creatorId,
                            Urgency = urgency
                        });
                    }
                }

                Log.Information("Найдено открытых заявок: {Count}", applications.Count);
                return applications;
            }
            catch (Google.GoogleApiException gae) when (gae.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log.Error(gae, "Spreadsheet not found: {SpreadsheetId}", _spreadsheetId);
                throw;
            }
            catch (OperationCanceledException)
            {
                Log.Warning("GetOpenApplicationsAsync cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при чтении из Google Sheets");
                throw;
            }
        }


        public async Task<List<Application>> GetOpenApplicationsByUserAsync(long telegramUserId)
        {
            var allOpenApps = await GetOpenApplicationsAsync();

            return allOpenApps
                .Where(a => a.TelegramUserId == telegramUserId)
                .OrderByDescending(a => a.DateTime)
                .ToList();
        }


        public async Task<List<(string Object, string Direction, int Total, int Open, int Closed)>> GetApplicationStatusReportAsync()
        {
            try
            {
                // Обновляем диапазон для включения всех нужных столбцов
                var range = $"{SheetName}!A:N";
                var request = _sheetsService.Spreadsheets.Values.Get(_spreadsheetId, range);
                var response = await request.ExecuteAsync();

                var applications = new List<Application>();
                if (response.Values != null && response.Values.Count > 1) // Пропускаем заголовок
                {
                    for (int i = 1; i < response.Values.Count; i++)
                    {
                        var row = response.Values[i];
                        try
                        {
                            // Используем правильные индексы столбцов
                            var status = row.Count > 1 ? row[1].ToString().Trim() : "Закрыто";  // Статус в столбце B
                            var obj = row.Count > 3 ? row[3].ToString() : "";                   // Объект в столбце D
                            var direction = row.Count > 4 ? row[4].ToString() : "";             // Направление в столбце E

                            applications.Add(new Application
                            {
                                RowId = i + 1,
                                Object = obj,
                                Direction = direction,
                                Status = status
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Ошибка при парсинге строки: {Row}", string.Join(", ", row));
                        }
                    }
                }

                // Группируем и подсчитываем статистику
                var grouped = applications
                    .GroupBy(a => new { a.Object, a.Direction })
                    .Select(g => (
                        g.Key.Object,
                        g.Key.Direction,
                        Total: g.Count(),
                        Open: g.Count(a => a.Status.Equals("Открыто", StringComparison.OrdinalIgnoreCase)),
                        Closed: g.Count(a => a.Status.Equals("Закрыто", StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                Log.Information("Отчет по заявкам сгенерирован: {Count} групп", grouped.Count);
                return grouped;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при получении отчета о статусах заявок, SpreadsheetId: {SpreadsheetId}", _spreadsheetId);
                throw;
            }
        }

        public async Task<List<Application>> GetApplicationsAsync(DateTime from, DateTime to) // 29.12
        {
            var range = $"{SheetName}!A:O";
            var response = await _sheetsService.Spreadsheets.Values
                .Get(_spreadsheetId, range)
                .ExecuteAsync();

            var result = new List<Application>();

            if (response.Values == null || response.Values.Count <= 1)
                return result;

            for (int i = 1; i < response.Values.Count; i++)
            {
                var row = response.Values[i];
                var date = ParseDateTime(row, 0); // A

                if (!date.HasValue || date < from || date > to)
                    continue;

                var app = new Application
                {
                    RowId = i + 1,
                    DateTime = date,
                    Status = row.Count > 1 ? row[1]?.ToString() : "",
                    ClosedAt = ParseDateTime(row, 2),

                    Object = row.Count > 3 ? row[3]?.ToString() : "",
                    Direction = row.Count > 4 ? row[4]?.ToString() : "",
                    Description = row.Count > 5 ? row[5]?.ToString() : "",

                    Location = row.Count > 14 ? row[14]?.ToString() : "",
                    Urgency = row.Count > 13 ? row[13]?.ToString() : "",

                    MediaFileIds = SplitLinks(row.Count > 7 ? row[7]?.ToString() : ""),
                    ClosureMediaLinks = SplitLinks(row.Count > 8 ? row[8]?.ToString() : ""),

                    CreatorTelegramId = SafeParseLong(row, 11),
                    ClosedByTelegramId = SafeParseLong(row, 12),
                };

                // Контакты
                if (row.Count > 6 && !string.IsNullOrWhiteSpace(row[6]?.ToString()))
                {
                    var contact = row[6].ToString().Split(' ', 2);
                    app.ContactName = contact[0];
                    if (contact.Length > 1)
                        app.ContactPhone = contact[1];
                }

                result.Add(app);
            }

            return result;
        }


        public async Task UpdateStatusAsync(int rowId, string newStatus, long closedByUserId, ITelegramBotClient botClient)
        {
            try
            {
                var closedByUserName = await GetTelegramUsernameAsync(botClient, closedByUserId);
                var now = DateTime.Now;

                // Формируем данные для обновления
                var valueRange = new ValueRange
                {
                    Values = new List<IList<object>>
                    {
                        new List<object>
                        {
                            newStatus,                                              // B - Статус
                            newStatus == "Закрыто" ? now.ToString("dd.MM.yyyy HH:mm:ss") : "", // C - Дата и время закрытия
                            closedByUserName,                                       // K - Telegram User закрывающего
                            closedByUserId.ToString()                              // M - ID Telegram закрывающего
                        }
                    }
                };

                // Обновляем каждый столбец по отдельности
                var ranges = new[]
                {
                    $"{SheetName}!B{rowId}",  // Статус
                    $"{SheetName}!C{rowId}",  // Дата закрытия
                    $"{SheetName}!K{rowId}",  // Telegram User закрывающего
                    $"{SheetName}!M{rowId}"   // ID закрывающего
                };

                for (int i = 0; i < ranges.Length; i++)
                {
                    var singleValueRange = new ValueRange
                    {
                        Values = new List<IList<object>>
                {
                    new List<object> { valueRange.Values[0][i] }
                }
                    };

                    var updateRequest = _sheetsService.Spreadsheets.Values.Update(singleValueRange, _spreadsheetId, ranges[i]);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                    await updateRequest.ExecuteAsync();
                }

                Log.Information("Статус заявки с ID {RowId} изменен на {Status} пользователем {UserId}", rowId, newStatus, closedByUserId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при обновлении статуса заявки с ID {RowId}", rowId);
                throw;
            }
        }



        /// <summary>
        /// Загружает фото из Telegram в Google Drive и возвращает публичную ссылку.
        /// </summary>
        public async Task<string> UploadPhotoToDriveAsync(string fileId, ITelegramBotClient botClient)
        {
            try
            {
                // Получаем файл из Telegram
                var file = await botClient.GetFileAsync(fileId);
                using (var memoryStream = new MemoryStream())
                {
                    await botClient.DownloadFileAsync(file.FilePath, memoryStream);
                    memoryStream.Position = 0;

                    // Загружаем файл в Google Drive
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File
                    {
                        Name = fileId + ".jpg",
                        Parents = new List<string> { _driveFolderId }
                    };

                    var request = _driveService.Files.Create(fileMetadata, memoryStream, "image/jpeg");
                    request.Fields = "id, webContentLink, webViewLink";
                    var uploadedFile = await request.UploadAsync();

                    if (uploadedFile.Status == Google.Apis.Upload.UploadStatus.Completed)
                    {
                        var driveFile = request.ResponseBody;

                        // Делаем файл публичным
                        var permission = new Google.Apis.Drive.v3.Data.Permission
                        {
                            Type = "anyone",
                            Role = "reader"
                        };
                        await _driveService.Permissions.Create(permission, driveFile.Id).ExecuteAsync();

                        return driveFile.WebViewLink ?? driveFile.WebContentLink;
                    }
                    else
                    {
                        Log.Warning("Не удалось загрузить фото {FileId} в Google Drive", fileId);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при загрузке фото {FileId} в Google Drive", fileId);
                return null;
            }
        }

        // ✅ Новый объединенный метод для загрузки фото и видео
        public async Task<string> UploadMediaToDriveAsync(string fileId, ITelegramBotClient botClient)
        {
            try
            {
                var file = await botClient.GetFileAsync(fileId);
                using (var memoryStream = new MemoryStream())
                {
                    await botClient.DownloadFileAsync(file.FilePath, memoryStream);
                    memoryStream.Position = 0;

                    // Определяем расширение файла
                    string fileName, mimeType;
                    if (file.FilePath.Contains("video"))
                    {
                        fileName = fileId + ".mp4";
                        mimeType = "video/mp4";
                    }
                    else
                    {
                        fileName = fileId + ".jpg";
                        mimeType = "image/jpeg";
                    }

                    var fileMetadata = new Google.Apis.Drive.v3.Data.File
                    {
                        Name = fileName,
                        Parents = new List<string> { _driveFolderId }
                    };

                    var request = _driveService.Files.Create(fileMetadata, memoryStream, mimeType);
                    request.Fields = "id, webContentLink, webViewLink";
                    var uploadedFile = await request.UploadAsync();

                    if (uploadedFile.Status == Google.Apis.Upload.UploadStatus.Completed)
                    {
                        var driveFile = request.ResponseBody;

                        var permission = new Google.Apis.Drive.v3.Data.Permission
                        {
                            Type = "anyone",
                            Role = "reader"
                        };
                        await _driveService.Permissions.Create(permission, driveFile.Id).ExecuteAsync();

                        Log.Information("Медиафайл {FileId} успешно загружен на Google Drive:  {Link}", fileId, driveFile.WebViewLink);
                        return driveFile.WebViewLink ?? driveFile.WebContentLink;
                    }
                    else
                    {
                        Log.Warning("Не удалось загрузить медиафайл {FileId} на Google Drive", fileId);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при загрузке медиафайла {FileId} на Google Drive", fileId);
                return null;
            }
        }

        // ✅ Объединенный метод для добавления ссылок на медиафайлы при закрытии
        public async Task AddClosureMediaAsync(int rowId, List<string> mediaLinks)
        {
            try
            {
                var media = string.Join(", ", mediaLinks);
                var valueRange = new ValueRange
                {
                    Values = new List<IList<object>>
            {
                new List<object> { media }
            }
                };

                var updateRequest = _sheetsService.Spreadsheets.Values.Update(valueRange, _spreadsheetId, $"{SheetName}!I{rowId}");
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                await updateRequest.ExecuteAsync();

                Log.Information("Медиафайлы подтверждения для строки {RowId} добавлены:  {Media}", rowId, media);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при добавлении медиафайлов подтверждения для строки {RowId}", rowId);
                throw;
            }
        }

        public async Task<(long creatorId, string status)> GetApplicationInfoAsync(int rowId)
        {
            try
            {
                var range = $"{SheetName}!A{rowId}:L{rowId}";
                var response = await _sheetsService.Spreadsheets.Values.Get(_spreadsheetId, range).ExecuteAsync();

                if (response.Values == null || response.Values.Count == 0)
                    throw new Exception($"Заявка с ID {rowId} не найдена.");

                var row = response.Values[0];
                var creatorId = row.Count > 11 ? Convert.ToInt64(row[11]) : 0;
                var status = row.Count > 7 ? row[7].ToString() : "";

                return (creatorId, status);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при получении информации о заявке {RowId}", rowId);
                throw;
            }
        }

        // Получить заявку по номеру строки (RowId)
        public async Task<Application> GetApplicationByRowIdAsync(int rowId)
        {
            if (rowId < 2) return null;

            var range = $"Заметки!A{rowId}:O{rowId}";
            var response = await _sheetsService.Spreadsheets.Values
                .Get(_spreadsheetId, range)
                .ExecuteAsync();

            var v = response.Values?.FirstOrDefault();
            if (v == null || v.Count == 0) return null;

            var app = new Application
            {
                RowId = rowId,
                DateTime = ParseDateTime(v, 0),       // A
                Status = v.Count > 1 ? v[1].ToString() : "",
                ClosedAt = ParseDateTime(v, 2),       // C

                Object = v.Count > 3 ? v[3].ToString() : "",       // D
                Direction = v.Count > 4 ? v[4].ToString() : "",    // E
                Description = v.Count > 5 ? v[5].ToString() : "",  // F

                Location = v.Count > 14 ? v[14].ToString() : "",   // O ✅
                Urgency = v.Count > 13 ? v[13].ToString() : "",    // N ✅

                MediaFileIds = SplitLinks(v.Count > 7 ? v[7].ToString() : ""),
                ClosureMediaLinks = SplitLinks(v.Count > 8 ? v[8].ToString() : ""),

                CreatorTelegramId = SafeParseLong(v, 11),  // L
                ClosedByTelegramId = SafeParseLong(v, 12), // M
            };


            // G — ФИО и контакты
            if (v.Count > 6 && !string.IsNullOrWhiteSpace(v[6].ToString()))
            {
                var contact = v[6].ToString().Trim().Split(' ', 2);
                app.ContactName = contact[0];
                if (contact.Length > 1)
                    app.ContactPhone = contact[1];
            }

            return app;
        }



        // Обновляет всю строку заявки в Google Sheets (A..M)
        public async Task UpdateEditableFieldsAsync(Application app)
        {
            var updates = new List<(string Range, string Value)>();
            int row = app.RowId;

            if (!string.IsNullOrWhiteSpace(app.Object))
                updates.Add(($"Заметки!D{row}", app.Object));

            if (!string.IsNullOrWhiteSpace(app.Direction))
                updates.Add(($"Заметки!E{row}", app.Direction));

            if (!string.IsNullOrWhiteSpace(app.Description))
                updates.Add(($"Заметки!F{row}", app.Description));

            if (!string.IsNullOrWhiteSpace(app.ContactName) || !string.IsNullOrWhiteSpace(app.ContactPhone))
                updates.Add(($"Заметки!G{row}", $"{app.ContactName} {app.ContactPhone}".Trim()));

            // ✅ Срочность → N
            if (!string.IsNullOrWhiteSpace(app.Urgency) ||
                !string.IsNullOrWhiteSpace(app.Deadline))
            {
                var urgencyValue = app.Urgency ?? "";
                if (!string.IsNullOrWhiteSpace(app.Deadline))
                {
                    urgencyValue = $"{urgencyValue} ({app.Deadline})";
                }

                updates.Add(($"Заметки!N{row}", urgencyValue));
            }

            // ✅ Местоположение → O
            if (!string.IsNullOrWhiteSpace(app.Location))
                updates.Add(($"Заметки!O{row}", app.Location));

            foreach (var (range, value) in updates)
            {
                var body = new ValueRange
                {
                    Values = new List<IList<object>>
            {
                new List<object> { value }
            }
                };

                var update = _sheetsService.Spreadsheets.Values.Update(body, _spreadsheetId, range);
                update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await update.ExecuteAsync();
            }

            Log.Information("Обновлены редактируемые поля для заявки #{RowId}", row);
        }

        /// <summary>
        /// Получает максимальный допустимый RowId (количество строк в листе - 1, т.к. строка 1 это заголово��)
        /// </summary>
        public async Task<int> GetMaxRowIdAsync()
        {
            try
            {
                var spreadsheet = await _sheetsService.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
                var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == SheetName);

                if (sheet == null)
                    return 0;

                int rowCount = sheet.Properties.GridProperties?.RowCount ?? 100;
                return rowCount; 
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при получении максимального RowId");
                return 0;
            }
        }

        // --------- ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ----------
        private static DateTime? ParseDateTime(IList<object> values, int index)
        {
            if (values == null || values.Count <= index) return null;
            var s = values[index]?.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, out var dt)) return dt;
            // попробуйте парсить альтернативные форматы, если нужно:
            if (DateTime.TryParseExact(s, new[] { "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt))
                return dt;
            return null;
        }

        private static long SafeParseLong(IList<object> values, int index)
        {
            if (values == null || values.Count <= index) return 0;
            var s = values[index]?.ToString();
            if (long.TryParse(s, out var v)) return v;
            return 0;
        }

        private static long? SafeParseLongNullable(IList<object> values, int index)
        {
            if (values == null || values.Count <= index) return null;
            var s = values[index]?.ToString();
            if (long.TryParse(s, out var v)) return v;
            return null;
        }

        private static List<string> ParsePhotoLinks(string links)
        {
            if (string.IsNullOrWhiteSpace(links)) return new List<string>();
            return links
                .Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
        }

        private static List<string> SplitLinks(string input)
        { 
            if (string.IsNullOrWhiteSpace(input)) return new List<string>();
            return input.Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }


    }
}