using DocumentFormat.OpenXml.Spreadsheet;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Oauth2.v2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Serilog;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegrame_Test;
using Telegrame_Test.Models;
using Telegrame_Test.Models.Proxy;
using Telegrame_Test.Service;
using Telegrame_Test.Services;

class Program
{
    static readonly ConcurrentDictionary<long, SemaphoreSlim> _userLocks = new();

    static SemaphoreSlim GetUserLock(long userId) =>
        _userLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    public static async Task Main(string[] args)
    {
       
        try
        {
            var host = CreateHostBuilder(args).Build();
            await RunHostAsync(host);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Критическая ошибка запуска бота");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((ctx, services) =>
            {
                var configuration = ctx.Configuration;
               
                // EF Core
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, "data", "users.db")}"));

                string ResolvePath(string? p)
                {
                    if (string.IsNullOrWhiteSpace(p))
                        return string.Empty;
                    if (Path.IsPathRooted(p))
                        return p;
                    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
                }

                // Кэш для конфигураций (IMemoryCache)
                services.AddMemoryCache();

                // Google Sheets (сервисный аккаунт)
                var saPath = configuration["Google:Sheets:ServiceAccountJson"];
                if (!string.IsNullOrEmpty(saPath))
                {
                    var saFullPath = ResolvePath(saPath);
                    var saCredential = GoogleCredential.FromFile(saFullPath)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);

                    services.AddSingleton(new SheetsService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = saCredential,
                        ApplicationName = "TelegramBot"
                    }));
                }

                // Google Drive (OAuth с refresh_token)
                CancellationToken ct = default;
                services.AddSingleton<UserCredential>(sp =>
                   GetOrRefreshCredentialAsync(configuration, ResolvePath, ct).GetAwaiter().GetResult());

                services.AddSingleton(sp =>
                {
                    var credential = sp.GetRequiredService<UserCredential>();
                    return new DriveService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = credential,
                        ApplicationName = "TelegramBot"
                    });
                });

                var advancedResilienceConfig = configuration.GetSection("Network:Resilience").Get<AdvancedResilienceConfig>() ?? new();

                services.AddSingleton<INetworkResilienceService, NetworkResilienceService>();
                services.AddSingleton<IAdaptiveBackoffService, AdaptiveBackoffService>();

                // HTTP клиенты с использованием NetworkResilienceService
                services.AddHttpClient("TelegramPolling")
                    .ConfigurePrimaryHttpMessageHandler((sp) =>
                    {
                        var networkService = sp.GetRequiredService<INetworkResilienceService>();
                        return networkService.CreateMessageHandler("TelegramPolling");
                    })
                    .SetHandlerLifetime(TimeSpan.FromMinutes(10))
                    .AddStandardResilienceHandler(options =>
                    {
                        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(advancedResilienceConfig.AttemptTimeoutSec);
                        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(advancedResilienceConfig.TotalTimeoutSec);

                        options.Retry.MaxRetryAttempts = advancedResilienceConfig.MaxRetryAttempts;
                        options.Retry.Delay = TimeSpan.FromMilliseconds(advancedResilienceConfig.InitialDelayMs);
                        options.Retry.BackoffType = DelayBackoffType.Exponential;
                        options.Retry.UseJitter = advancedResilienceConfig.UseJitter;

                        options.Retry.ShouldHandle = args =>
                            new ValueTask<bool>(
                                args.Outcome.Exception is HttpRequestException or OperationCanceledException or TimeoutException ||
                                args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests ||
                                (int)args.Outcome.Result?.StatusCode >= 500
                            );

                        options.CircuitBreaker.FailureRatio = advancedResilienceConfig.CircuitBreakerFailureRatio;
                        options.CircuitBreaker.MinimumThroughput = advancedResilienceConfig.CircuitBreakerMinimumThroughput;
                        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(advancedResilienceConfig.CircuitBreakerBreakDurationSec);
                    });

                services.AddHttpClient("TelegramSend")
                    .ConfigurePrimaryHttpMessageHandler((sp) =>
                    {
                        var networkService = sp.GetRequiredService<INetworkResilienceService>();
                        return networkService.CreateMessageHandler("TelegramSend");
                    })
                    .AddStandardResilienceHandler(options =>
                    {
                        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(advancedResilienceConfig.AttemptTimeoutSec);
                        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(advancedResilienceConfig.TotalTimeoutSec);
                        options.Retry.MaxRetryAttempts = advancedResilienceConfig.MaxRetryAttempts;
                        options.Retry.Delay = TimeSpan.FromMilliseconds(advancedResilienceConfig.InitialDelayMs);
                        options.Retry.BackoffType = DelayBackoffType.Exponential;
                        options.Retry.UseJitter = advancedResilienceConfig.UseJitter;

                        options.Retry.ShouldHandle = args =>
                            new ValueTask<bool>(
                                args.Outcome.Exception is HttpRequestException or OperationCanceledException or TimeoutException ||
                                args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests ||
                                (int)args.Outcome.Result?.StatusCode >= 500
                            );

                        options.CircuitBreaker.FailureRatio = advancedResilienceConfig.CircuitBreakerFailureRatio;
                        options.CircuitBreaker.MinimumThroughput = advancedResilienceConfig.CircuitBreakerMinimumThroughput;
                        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(advancedResilienceConfig.CircuitBreakerBreakDurationSec);
                    });


                // Остальные сервисы
                services.AddSingleton<ITelegramPollingClient, TelegramPollingClient>();
                services.AddSingleton<ITelegramSendClient, TelegramSendClient>();
                services.AddSingleton<DirectionalSheetsService>();
                services.AddSingleton<UserStateService>();
                services.AddSingleton<GoogleSheetsService>();
                services.AddSingleton<DailyReminderService>();
                services.AddSingleton<UserDatabaseService>();
                services.AddScoped<TelegramMessageService>();
                services.AddSingleton<InvitationService>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();
                    var userDb = sp.GetRequiredService<UserDatabaseService>();
                    var dbCtx = sp.GetRequiredService<ApplicationDbContext>();
                    return new InvitationService(cfg, userDb, dbCtx);
                });

                // Background services
                services.AddHostedService<DailyReminderHostedService>();
                services.AddHostedService<InvitationCleanupHostedService>();
                services.AddHostedService<StateCleanupBackgroundService>();
            })
            .UseSerilog((ctx, lc) => lc
                .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Extensions.Http.Resilience", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Extensions.Resilience", LogEventLevel.Warning)
                    .MinimumLevel.Override("Polly", LogEventLevel.Warning)

                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "data", "logs", "bot-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            );

    // Оптимизированный запуск (полностью async)
    private static async Task RunHostAsync(IHost host)
    {
        await host.StartAsync();

        // Инициализация GoogleSheetsService
        using var scope = host.Services.CreateScope();
        var gsService = scope.ServiceProvider.GetRequiredService<GoogleSheetsService>();
        await gsService.InitializeAsync();

        var pollingBot = host.Services.GetRequiredService<ITelegramPollingClient>().Client;
        var botClient = host.Services.GetRequiredService<ITelegramSendClient>().Client;

        var userStateService = host.Services.GetRequiredService<UserStateService>();
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var backoffService = host.Services.GetRequiredService<IAdaptiveBackoffService>();

        // Webhook для ускорения (если в конфиге есть WebhookUrl, иначе fallback на polling)
        var webhookUrl = configuration["Telegram:WebhookUrl"];
        if (!string.IsNullOrEmpty(webhookUrl))
        {
            await pollingBot.SetWebhookAsync(webhookUrl);
            Log.Information("Webhook установлен на {Url}", webhookUrl);
        }
        else
        {
            await pollingBot.DeleteWebhookAsync(true);
            pollingBot.StartReceiving(
                updateHandler: async (bot, update, ct) =>
                {
                    try
                    {
                       await HandleUpdateAsync(host, botClient, update, userStateService, ct, configuration, backoffService);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Ошибка в HandleUpdateAsync, updateId={UpdateId}", update.Id);
                    }
                },
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>(), ThrowPendingUpdates = false },
                cancellationToken: lifetime.ApplicationStopping);
            Log.Information("Polling запущен (fallback)");
        }

        // Проверка чатов (async версия)
        Console.WriteLine("\nПроверка доступа к чатам...");
        var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>();
        if (chatMappings != null && chatMappings.Any())
        {
            var uniqueChats = chatMappings.GroupBy(cm => cm.ChatId).Select(g => new { g.Key, Count = g.Count() }).ToList();
            foreach (var chatGroup in uniqueChats)
            {
                try
                {
                    var chat = await pollingBot.GetChatAsync(chatGroup.Key);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"ChatId {chatGroup.Key}: {chat.Title ?? "Private Chat"} (используется {chatGroup.Count} маршрутом)");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"ChatId {chatGroup.Key}: ОШИБКА - {ex.Message}");
                }
                Console.ResetColor();
            }
        }

        // Команды бота (async, без дублирующих delete)
        await pollingBot.DeleteMyCommandsAsync();
        await pollingBot.SetMyCommandsAsync(new List<BotCommand>
        {
            new BotCommand { Command = "start", Description = "Создать новую заявку" },
            new BotCommand { Command = "close", Description = "Закрыть заявку (только для админов)" },
            new BotCommand { Command = "edit", Description = "Редактирование заявки" },
            new BotCommand { Command = "myapplications", Description = "Список моих заявок"},
            new BotCommand { Command = "redirect", Description = "Перенаправить заявку (только для админов)" },
            new BotCommand { Command = "testread", Description = "Показать список открытых заявок (только для админов)" },
            new BotCommand { Command = "weeklyreport", Description = "Сформировать еженедельный отчет (только для админов)" },
            new BotCommand { Command = "allsreport", Description = "Сформировать общий отчет (только для админов)"},
            new BotCommand { Command = "generateinvite", Description = "Создать приглашение (только для админов)" },
            new BotCommand { Command = "feedback", Description = "Пожелания и предложения" },
            new BotCommand { Command = "info", Description = "Информация о боте" }
        }, scope: new BotCommandScopeAllPrivateChats());

        Log.Information("Бот запущен.");
        await Task.Delay(Timeout.Infinite, lifetime.ApplicationStopping);
    }

    // =============================================================================
    // КЛЮЧЕВОЙ МЕТОД: пытается взять токен из конфига → если не работает → OAuth → сохраняет в appsettings.json
    // =============================================================================
    private static async Task<UserCredential> GetOrRefreshCredentialAsync(IConfiguration config, Func<string?, string> resolvePath, CancellationToken ct)
    {
        var clientSecretsPath = resolvePath(config["Google:OAuth:ClientSecretsPath"]);
        var currentRefreshToken = config["Google:OAuth:RefreshToken"];

        if (!System.IO.File.Exists(clientSecretsPath))
            throw new FileNotFoundException("Не найден client.json", clientSecretsPath);

        using var stream = new FileStream(clientSecretsPath, FileMode.Open, FileAccess.Read);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

        // 1. Пробуем использовать токен из appsettings.json
        if (!string.IsNullOrWhiteSpace(currentRefreshToken))
        {
            try
            {
                var credential = await CreateCredentialFromRefreshToken(secrets, currentRefreshToken);
                Serilog.Log.Information("Успешно авторизован через refresh_token из appsettings.json");
                return credential;
            }
            catch (Exception ex) when (ex.Message.Contains("invalid_grant") || ex.Message.Contains("revoked"))
            {
                Serilog.Log.Warning("Refresh_token недействителен или отозван. Будет запрошена повторная авторизация...");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Не удалось использовать refresh_token из конфигурации");
            }
        }

        // 2. Fallback: открываем браузер для авторизации
        Serilog.Log.Warning("Открывается браузер для авторизации Google (это происходит только при первом запуске или если токен отозван)...");

        var credentialFromBrowser = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            new[] { DriveService.Scope.Drive },
            "user",
            ct);

        // 3. Сохраняем новый refresh_token в appsettings.json
        if (!string.IsNullOrWhiteSpace(credentialFromBrowser.Token.RefreshToken))
        {
            SaveRefreshTokenToConfig(credentialFromBrowser.Token.RefreshToken);
        }

        return credentialFromBrowser;
    }

    // Вспомогательный: создание credential из refresh_token
    private static async Task<UserCredential> CreateCredentialFromRefreshToken(ClientSecrets secrets, string refreshToken)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets
        });

        var token = new TokenResponse { RefreshToken = refreshToken };
        var credential = new UserCredential(flow, "user", token);

        await credential.RefreshTokenAsync(CancellationToken.None); // обновит access_token
        return credential;
    }

    private static void SaveRefreshTokenToConfig(string newRefreshToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!System.IO.File.Exists(configPath))
        {
            Serilog.Log.Error("Не найден appsettings.json — не могу сохранить refresh_token");
            return;
        }

        try
        {
            var jsonText = System.IO.File.ReadAllText(configPath);
            var jsonNode = JsonNode.Parse(jsonText)
                           ?? throw new InvalidOperationException("Не удалось распарсить appsettings.json");

            // Самая главная строка — просто присваиваем по пути
            jsonNode["Google"]!["OAuth"]!["RefreshToken"] = newRefreshToken;

            // Сохраняем красиво с отступами
            System.IO.File.WriteAllText(configPath,
                jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Serilog.Log.Information("Новый refresh_token успешно сохранён в appsettings.json");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка при сохранении refresh_token в appsettings.json");
        }
    }


    static async Task HandleUpdateAsync(IHost host, ITelegramBotClient botClient, Update update, UserStateService userStateService, CancellationToken cancellationToken, IConfiguration configuration, IAdaptiveBackoffService backoffService)
    {
        // Быстрые фильтры
        if (update.Type != UpdateType.Message || update.Message == null)
            return;

        var message = update.Message;
        if (message.Chat == null || message.Chat.Type != ChatType.Private)
        {
            // Игнорируем групповые сообщения (callback'и из групп отдельно обрабатываются ниже)
            if (update.CallbackQuery?.Message?.Chat?.Type != ChatType.Private)
            {
                return;
            }
            return;
        }

        var userId = message.From.Id;

        // Получаем сервисы один раз (эффективнее, чем каждый раз RequestService)
        var services = host.Services;
        var userDbService = services.GetRequiredService<UserDatabaseService>();
        var googleSheetsService = services.GetRequiredService<GoogleSheetsService>();
        var invitationService = services.GetRequiredService<InvitationService>();
        var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>() ?? new List<ChatMapping>();
        var admins = configuration.GetSection("Admins").Get<List<string>>() ?? new List<string>();

        // Локальное кеширование результатов проверки пользователя (слайдинг/TTL)
        var cache = services.GetRequiredService<IMemoryCache>();

        // Загружаем состояние пользователя (или создаём новый объект)
        var userState = await userStateService.GetUserState(userId);
        userState ??= new UserState { Application = new Application { TelegramUserId = userId, DateTime = DateTime.Now } };

        // --- CALLBACK QUERY handling (ранний возврат) ---
        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
        {
            var callback = update.CallbackQuery;
            var fromUser = callback.From.Id;

            if (callback.Data == "back")
            {
                if (userState.PreviousState != DialogState.None)
                {
                    userState.State = userState.PreviousState;
                    userState.PreviousState = GetPreviousState(userState.State);
                    await userStateService.UpdateUserState(fromUser, userState);
                    await ShowCurrentStep(botClient, userState, fromUser, configuration, CancellationToken.None, googleSheetsService, userStateService);
                }
                else
                {
                    await botClient.AnswerCallbackQueryAsync(callback.Id, "Нет предыдущего шага.", cancellationToken: cancellationToken);
                }

                await botClient.AnswerCallbackQueryAsync(callback.Id, cancellationToken: cancellationToken);
                return;
            }

            // Если data соответствует объекту/направлению — обработать
            if (chatMappings.Any(cm => cm.Object == callback.Data || cm.Direction == callback.Data))
            {
                await ProcessCallbackData(host, botClient, callback, userState, userStateService, configuration, cancellationToken);
            }

            await botClient.AnswerCallbackQueryAsync(callback.Id, cancellationToken: cancellationToken);
            return;
        }

        // --- Хелпер: кэшированная проверка доступа пользователя ---
        static async Task<bool> IsUserAllowedCached(UserDatabaseService db, IMemoryCache cacheLocal, long id)
        {
            var key = $"user_allowed_{id}";
            if (cacheLocal.TryGetValue<bool>(key, out var cached)) return cached;
            var allowed = await db.IsUserAllowed(id);                                   
            cacheLocal.Set(key, allowed, TimeSpan.FromMinutes(15));
            return allowed;
        }

        // --- Обработка команд (быстрый путь) ---
        var text = message.Text?.Trim() ?? string.Empty;

        // /testread -- сначала проверяем права, потом получаем данные
        if (text == "/testread")
        {
            if (!await userDbService.IsUserAdmin(userId))
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "❌ У вас нет прав для просмотра всех заявок. Эта функция доступна только администраторам.",
                        cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            var applications = await googleSheetsService.GetOpenApplicationsAsync();
            if (!applications.Any())
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "📭 Нет открытых заявок.",
                        cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            // Формируем и отправляем пачками — ваша существующая логика (без изменений, можно вынести в метод)
            var lines = new List<string>();
            foreach (var app in applications)
            {
                lines.Add(
                    $"📝 Заявка #{app.RowId}\n📅 {app.DateTime:dd.MM.yyyy HH:mm}\n🏢 {app.Object}\n🔧 {app.Direction}\n📍 {app.Description}\n👤 {app.ContactName} {app.ContactPhone}\n📎 {(app.MediaFileIds.Any() ? "Есть" : "Нет")}"
                );
                lines.Add("─────────────────");
            }

            const int maxLength = 4096;
            var batch = new StringBuilder();
            foreach (var line in lines)
            {
                if (batch.Length + line.Length + 2 > maxLength)
                {
                    var sendText = batch.ToString();
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, sendText, parseMode: ParseMode.Html, disableWebPagePreview: true, cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    batch.Clear();
                }
                batch.AppendLine(line);
            }

            if (batch.Length > 0)
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, batch.ToString(), parseMode: ParseMode.Html, disableWebPagePreview: true, cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
            }

            await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, $"📊 Всего открытых заявок: {applications.Count}", cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }

        // /myapplications - быстрый путь
        if (text == "/myapplications")
        {
            var myApps = await googleSheetsService.GetOpenApplicationsByUserAsync(userId);
            if (!myApps.Any())
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "📭 У вас нет открытых заявок.", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            // сборка и отправка аналогично /testread
            var lines = myApps.Select(app =>
                $"📝 <b>Заявка #{app.RowId}</b>\n📅 {app.DateTime:dd.MM.yyyy HH:mm}\n🏢 {app.Object}\n🔧 {app.Direction}\n📍 {app.Description}\n👤 {app.ContactName} {app.ContactPhone}\n⚠Приоритет: {app.Urgency}\n📎 {(app.MediaFileIds.Any() ? "Есть" : "Нет")}"
            ).ToList();

            const int maxLen = 4096;
            var batch2 = new StringBuilder();
            foreach (var line in lines)
            {
                if (batch2.Length + line.Length + 2 > maxLen)
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, batch2.ToString(), parseMode: ParseMode.Html, disableWebPagePreview: true, cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    batch2.Clear();
                }
                batch2.AppendLine(line);
                batch2.AppendLine("─────────────────");
            }
            if (batch2.Length > 0)
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, batch2.ToString(), parseMode: ParseMode.Html, disableWebPagePreview: true, cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
            }

            await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, $"📊 Всего ваших открытых заявок: {myApps.Count}", cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (text == "/diagchat")
        {
            var diagnosticService = new ChatIdDiagnosticService(botClient);
            diagnosticService.LogChatInfo(message);

            await TrySendAsync(ct => botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"📊 <b>Информация о чате:</b>\n\n<b>ChatId:</b> <code>{message.Chat.Id}</code>\n<b>Тип:</b> {message.Chat.Type}\n<b>Название:</b> {message.Chat.Title ?? "N/A"}\n<b>Username:</b> @{(message.Chat.Username ?? "N/A")}\n\n✅ ID скопирован в консоль для диагностики",
                parseMode: ParseMode.Html,
                cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (text == "/listadmin")
        {
            var sb = new StringBuilder("Список админов:\n\n");
            foreach (var adminId in admins)
            {
                try
                {
                    var chat = await botClient.GetChatAsync(adminId, cancellationToken: cancellationToken);
                    var username = string.IsNullOrEmpty(chat.Username) ? "" : $" (@{chat.Username})";
                    var fullName = string.IsNullOrEmpty(chat.FirstName) ? "Unknown" : chat.FirstName;
                    sb.AppendLine($"- [ {fullName}{username} ](tg://user?id={adminId})");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка при получении информации о админе {AdminId}", adminId);
                    sb.AppendLine($"- ID: {adminId} (Unknown user)");
                }
            }

            await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, sb.ToString(), parseMode: ParseMode.Markdown, cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (text == "/close")
        {
            if (!await userDbService.IsUserAdmin(userId))
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "❌ У вас нет прав для закрытия заявок. Эта функция доступна только администраторам.", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }
            Log.Information("Закрытие заявки пользователем {UserId}", userId);
            var keyboard = new ReplyKeyboardMarkup(new[] { new KeyboardButton("Отмена") }) { ResizeKeyboard = true };
            userState.State = DialogState.WaitingForCloseRowId;
            await userStateService.UpdateUserState(userId, userState);

            await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "📋 Укажите ID заявки для закрытия (например: 2, 3, 4...):", replyMarkup: keyboard, cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (text == "/weeklyreport" || text == "/allsreport")
        {
            if (!await userDbService.IsUserAdmin(userId))
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "❌ У вас нет прав для генерации отчетов. Эта функция доступна только администраторам.", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }
            var reminderService = services.GetRequiredService<DailyReminderService>();
            if (text == "/weeklyreport")
                await reminderService.SendWeeklyReportAsync(message.Chat.Id);
            else
                await reminderService.SendAllReportAsync(message.Chat.Id);

            return;
        }
        else if (text.StartsWith("/generateinvite"))
        {
            if (!await userDbService.IsUserAdmin(userId))
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "❌ У вас нет прав для создания приглашений. Эта функция доступна только администраторам.", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            var inviteLink = invitationService.GenerateInvitation(userId);
            await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, $"🔗 Создано приглашение: {inviteLink}\nСрок действия: 24 часа.", cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (text == "/feedback")
        {
            var feedbackFormUrl = configuration["Google:FeedbackFormUrl"]; 
            Serilog.Log.Information("Пользователь заполняет Google форму {UserId}", userId);
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"Спасибо за интерес к улучшению бота! Ваши предложения помогут нам стать лучше.\n\nЗаполните короткую форму:\n{feedbackFormUrl}\n\nВаши ответы сохранятся в нашей таблице для анализа.",
                cancellationToken: cancellationToken);
            return;
        }
        else if (text == "/info")
        {
            var infoText =
                "🤖 <b>Бот приёма и контроля заявок УГМК-Здоровье</b>\n\n" +

                "Этот бот предназначен для создания, сопровождения и закрытия технических заявок.\n\n" +

                "📝 <b>Основные возможности:</b>\n" +
                "• Создание заявки определенного формата\n" +
                "• Прикрепление медиафайлов в зявках\n" +
                "• Установка приоритетов и дедлайна\n" +
                "• Просмотр своих заявок\n" +
                "• Уведомление о выполненых заявках\n\n" +

                "👤 <b>Команды для пользователей:</b>\n" +
                "/start — начать работу с ботом\n" +
                "/myapplications — мои открытые заявки\n" +
                "/edit - редактирование заявки\n" +
                "/info — информация о боте\n\n" +

                "ℹ️ <i>Если бот ожидает от вас ввод данных — используйте кнопки на выведенной клавиатуре.</i>";
            await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, infoText, parseMode: ParseMode.Html, disableWebPagePreview: true, cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (message?.Text == "/edit")
        {
            // Получаем список заявок
            var apps = await googleSheetsService.GetOpenApplicationsAsync();
            var isAdmin = admins.Contains(userId.ToString()) || await userDbService.IsUserAdmin(userId); // Проверка админа

            if (!isAdmin)
            {
                apps = apps.Where(a => a.TelegramUserId == userId).ToList();
                if (!apps.Any())
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "У вас нет открытых заявок для редактирования.", cancellationToken: cancellationToken);
                    return;
                }
            }

            // Кнопки с заявками
            var rows = apps
                .Select(a => new[] { new KeyboardButton($"#{a.RowId} | {a.Object} | {a.Direction}") })
                .ToArray();

            var keyboard = new ReplyKeyboardMarkup(rows)
            {
                ResizeKeyboard = true
            };
            Log.Information("Редактирование заявки пользователем {UserId}", userId);

            userState.State = DialogState.WaitingForEditSelection;
            await userStateService.UpdateUserState(userId, userState);

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Выберите заявку для редактирования:",
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);

            return;
        }
        else if (text == "/redirect")
        {
            if (!await userDbService.IsUserAdmin(userId))
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        "❌ У вас нет прав для перенаправления заявок. Эта функция доступна только администраторам.",
                        cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            Log.Information("Перенаправление заявки инициировано пользователем {UserId}", userId);
            var keyboard = new ReplyKeyboardMarkup(new[] { new KeyboardButton("Отмена") }) { ResizeKeyboard = true };
            userState.State = DialogState.WaitingForRedirectRowId;
            await userStateService.UpdateUserState(userId, userState);

            await TrySendAsync(ct => botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "📋 Укажите ID заявки для перенаправления (например: 2, 3, 4...):",
                    replyMarkup: keyboard,
                    cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);
            return;
        }
        else if (text.StartsWith("/start"))
        {
            var args = text.Split(' ', 2);
            Log.Debug("Обработка /start для пользователя {UserId}. Args={Args}", userId, args);

            // 1) /start с токеном — одноразовая логика приглашения
            if (args.Length > 1)
            {
                if (invitationService.ValidateInvitation(args[1], out long invitedBy, userId))
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, $"Добро пожаловать! Доступ выдан администратором {invitedBy}. Используйте /start.", cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);

                    return;
                }

                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "Недействительная или истекшая ссылка приглашения.", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            // 2) Проверка доступа — через кэш (минимизируем обращения в БД)
            if (!await IsUserAllowedCached(userDbService, cache, userId))
            {
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "❌ У вас нет доступа к боту. Обратитесь к админиу", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            // 3) Если пользователь в edit-flow или close-flow — считаем /start как reset новой заявки
            var editStates = new HashSet<DialogState> {
                DialogState.WaitingForEditSelection, DialogState.ChoosingEditField, DialogState.EditingUrgencyPick,
                DialogState.EditingDeadlinePick, DialogState.EditingTimePick, DialogState.EditingField,
                DialogState.WaitingForCloseRowId, DialogState.WaitingForClosePhoto, DialogState.WaitingForCloseContact, DialogState.WaitingForCloseComment
            };

            // Сериализация: защищаем манипуляции со state семафором
            var userLock = GetUserLock(userId);
            if (!await userLock.WaitAsync(5000)) // ждем до 5s на семафор
            {
                Log.Warning("Cannot acquire user lock for /start processing for user {UserId}", userId);
                // аккуратно уведомим пользователя
                await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "Подождите немного и повторите /start.", cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;
            }

            try
            {
                if (userState.State != DialogState.None && editStates.Contains(userState.State))
                {
                    Log.Information("Reset user state from edit flow. UserId={UserId}, PrevState={State}", userId, userState.State);
                    userState.Application = new Application { TelegramUserId = userId, DateTime = DateTime.Now };
                    userState.MediaFileIds = new List<string>();
                    userState.ClosureMediaFileIds = new List<string>();
                    userState.SelectedDirectionCategory = null;
                    userState.PreviousState = DialogState.None;
                    userState.LastShownState = DialogState.None;
                    userState.State = DialogState.None;
                    await userStateService.UpdateUserState(userId, userState);
                    // продолжаем как новый старт
                }

                // Если пользователь уже в процессе (не edit-flow)
                if (userState.State != DialogState.None)
                {
                    Log.Information("Повторный /start от пользователя {UserId}, текущее состояние=\"{State}\"", userId, userState.State);

                    if (userState.LastShownState == userState.State)
                        return; // уже показывали — игнорируем

                    var ok = await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    if (!ok)
                    {
                        await TrySendAsync(ct => botClient.SendTextMessageAsync(userId, "Временная проблема сети — попробуйте ещё раз.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct),
                            userId, userState.State, CancellationToken.None, backoffService);
                    }
                    return;
                }

                // 4) Новый старт — создаём состояние и показываем первый шаг
                Log.Information("Старт нового диалога для пользователя {UserId}", userId);
                userState.Application = new Application { TelegramUserId = userId, DateTime = DateTime.Now };
                userState.State = DialogState.WaitingForObject;
                userState.PreviousState = DialogState.None;
                userState.LastShownState = DialogState.None;
                await userStateService.UpdateUserState(userId, userState);

                var started = await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                if (!started)
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(message.Chat.Id, "Не удалось начать диалог — проблема с сетью. Попробуйте позже.", cancellationToken: ct),
                        userId, userState.State, CancellationToken.None, backoffService);
                }
                return;
            }
            finally
            {
                userLock.Release();
            }
        }

        switch (userState.State)
        {
            case DialogState.WaitingForObject:
            case DialogState.WaitingForDirectionCategory:
            case DialogState.WaitingForDirection:
            case DialogState.WaitingForLocation:
            case DialogState.WaitingForUrgency:
            case DialogState.WaitingForDeadline:
            case DialogState.WaitingForTime:
            case DialogState.WaitingForDescription:
            case DialogState.WaitingForPhoto:
            case DialogState.WaitingForContact:
                await ProcessMessage(host, botClient, message, userState, userStateService, configuration, cancellationToken);
                break;

            case DialogState.WaitingForEditSelection:
            case DialogState.ChoosingEditField:
            case DialogState.EditingUrgencyPick:
            case DialogState.EditingDeadlinePick:
            case DialogState.EditingTimePick:
            case DialogState.EditingField:
                await ProcessEditMessage(host, botClient, message, userState, userStateService, configuration, cancellationToken);
                break;

            case DialogState.WaitingForCloseRowId:
            case DialogState.WaitingForClosePhoto:
            case DialogState.WaitingForCloseContact:
            case DialogState.WaitingForCloseComment:
                await ProcessClosePhoto(host, botClient, message, userState, userStateService, configuration, cancellationToken);
                break;

            case DialogState.WaitingForRedirectRowId:
            case DialogState.WaitingForRedirectChat:
                await HandleRedirectAsync(host, botClient, message, userState, userStateService, configuration, cancellationToken);
                break;


            default:
                // Нечего делать — безопасный noop
                Log.Debug("Unhandled state {State} for user {UserId}", userState.State, userId);
                break;
        }
    }


    static async Task ProcessCallbackData(IHost host, ITelegramBotClient botClient, CallbackQuery callbackQuery, UserState userState, UserStateService userStateService, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var chatType = callbackQuery?.Message?.Chat?.Type;
        if (chatType != null && chatType != ChatType.Private)
        {
            Log.Information("Игнорируем callback из группового чата: ChatType={ChatType}", chatType);
            return;
        }

        var userId = callbackQuery.From.Id;
        var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>();
        var googleSheetsService = host.Services.GetRequiredService<GoogleSheetsService>();

        if (userState.State == DialogState.WaitingForObject && (chatMappings?.Any(cm => cm.Object == callbackQuery.Data) ?? false))
        {
            userState.Application.Object = callbackQuery.Data;
            userState.State = DialogState.WaitingForDirection;
            userState.PreviousState = DialogState.WaitingForObject;
            await userStateService.UpdateUserState(userId, userState);
            await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
        }
        else if (userState.State == DialogState.WaitingForDirection && (chatMappings?.Any(cm => cm.Object == userState.Application.Object && cm.Direction == callbackQuery.Data) ?? false))
        {
            userState.Application.Direction = callbackQuery.Data;
            // Переходим к уточнению местонахождения (новый этап)
            userState.State = DialogState.WaitingForLocation;
            userState.PreviousState = DialogState.WaitingForDirection;
            await userStateService.UpdateUserState(userId, userState);
            await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
        }
        else if (callbackQuery.Data == "skip" && userState.State == DialogState.WaitingForPhoto)
        {
            userState.Application.MediaFileIds = null;
            userState.State = DialogState.WaitingForContact;
            userState.PreviousState = DialogState.WaitingForPhoto;
            await userStateService.UpdateUserState(userId, userState);
            await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
        }
    }


    static async Task ProcessMessage(IHost host, ITelegramBotClient botClient, Message message, UserState userState, UserStateService userStateService, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var userId = message.From.Id;
        var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>();
        var chatType = message.Chat.Type;
        if (chatType != ChatType.Private)
        {
            Log.Information("Игнорирую сообщение из группового чата: ChatType={ChatType}", chatType);
            return; // Выходим, не обрабатывая
        }

        var googleSheetsService = host.Services.GetRequiredService<GoogleSheetsService>();
        var dbContext = host.Services.GetRequiredService<ApplicationDbContext>();
        var tgMessageService = host.Services.GetRequiredService<TelegramMessageService>();

        if (userState.State == DialogState.None)
        {
            userState.OriginalCreatorId = userState.OriginalCreatorId ?? userId; // Устанавливаем ID создателя, если он еще не задан
            userState.Application.TelegramUserId = userId; // Также фиксируем в Application
        }

        if (message.Text == "Назад")
        {
            if (userState.PreviousState != DialogState.None)
            {
                userState.State = userState.PreviousState;
                userState.PreviousState = GetPreviousState(userState.State);
                await userStateService.UpdateUserState(userId, userState);
                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Нет предыдущего шага.",
                    replyMarkup: new ReplyKeyboardRemove(),
                    cancellationToken: cancellationToken);
            }
            return;
        }

        switch (userState.State)
        {
            case DialogState.WaitingForObject:
                if (chatMappings?.Any(cm => cm.Object == message.Text) ?? false)
                {
                    userState.Application.Object = message.Text;
                    userState.State = DialogState.WaitingForDirectionCategory; 
                    userState.PreviousState = DialogState.WaitingForObject;
                    userState.Application.CreatorTelegramId = message.From.Id;
                    userState.Application.TelegramUserId = message.From.Id; // Фиксируем ID создателя 09.10.2025 {\\\\}
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                }
                else if (string.Equals(message.Text, "Отмена", StringComparison.OrdinalIgnoreCase))
                {
                    userState.State = DialogState.None;
                    await userStateService.UpdateUserState(userId, userState);
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Операция отменена.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return;
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Пожалуйста, выберите объект из списка.",
                        cancellationToken: cancellationToken);
                }
                break;

            case DialogState.WaitingForDirectionCategory:
                var availableCategoryNames = GetAvailableCategoryNamesForObject(
                    configuration,
                    chatMappings,
                    userState.Application.Object);

                if (availableCategoryNames.Contains(message.Text))
                {
                    userState.SelectedDirectionCategory = message.Text;
                    userState.State = DialogState.WaitingForDirection;
                    userState.PreviousState = DialogState.WaitingForDirectionCategory;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                }
                else if (message.Text == "Назад")
                {
                    userState.State = DialogState.WaitingForObject;
                    userState.PreviousState = DialogState.None;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Пожалуйста, выберите категорию из списка.",
                        cancellationToken: cancellationToken);
                }
                break;

            case DialogState.WaitingForDirection:
                var availableDirectionDisplayNames = GetAvailableDirectionsByCategory(
                    configuration,
                    chatMappings,
                    userState.Application.Object,
                    userState.SelectedDirectionCategory);

                if (availableDirectionDisplayNames.Contains(message.Text))
                {
                    var internalDirectionName = GetDirectionInternalName(
                        configuration,
                        userState.SelectedDirectionCategory,
                        message.Text);

                    userState.Application.Direction = internalDirectionName;
                    userState.State = DialogState.WaitingForLocation;
                    userState.PreviousState = DialogState.WaitingForDirection;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    
                }
                else if (message.Text == "Назад")
                {
                    userState.State = DialogState.WaitingForDirectionCategory;
                    userState.PreviousState = DialogState.WaitingForObject;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Пожалуйста, выберите направление из списка.",
                        cancellationToken: cancellationToken);
                }
                break;

            case DialogState.WaitingForLocation:
                // Сохраняем местоположение и переходим к срочности/сроку
                userState.Application.Location = message.Text?.Trim();
                userState.State = DialogState.WaitingForUrgency;
                userState.PreviousState = DialogState.WaitingForLocation;
                await userStateService.UpdateUserState(userId, userState);
                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                break;

            case DialogState.WaitingForUrgency:
                var urgencyInput = message.Text?.Trim() ?? "";
                if (urgencyInput.Equals("🔴 НЕМЕДЛЕННО (выполнение до 30 мин)", System.StringComparison.OrdinalIgnoreCase))
                {
                    userState.Application.Urgency = "🔴 НЕМЕДЛЕННО (выполнение до 30 мин)";
                }
                else if (urgencyInput.Equals("\U0001f7e1 СРОЧНО (выполнение до 2-4 часов)", System.StringComparison.OrdinalIgnoreCase))
                {
                    userState.Application.Urgency = "\U0001f7e1 СРОЧНО (выполнение до 2-4 часов)";
                }
                else if (urgencyInput.Equals("\U0001f7e2 В РАБОЧИЙ ДЕНЬ (выполнение до 8-12 часов)", System.StringComparison.OrdinalIgnoreCase))
                {
                    userState.Application.Urgency = "\U0001f7e2 В РАБОЧИЙ ДЕНЬ (выполнение до 8-12 часов)";
                }
                else if (urgencyInput.Equals("🔵 ПЛАНОВЫЙ (выполнение 3-5 дней)", System.StringComparison.OrdinalIgnoreCase))
                {
                    userState.Application.Urgency = "🔵 ПЛАНОВЫЙ (выполнение 3-5 дней)";
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Пожалуйста, выберите приоритет.",
                        cancellationToken: cancellationToken);
                    return;
                }

                userState.State = DialogState.WaitingForDeadline; // Переход к выбору дедлайна
                userState.PreviousState = DialogState.WaitingForUrgency;
                await userStateService.UpdateUserState(userId, userState);
                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                break;

            case DialogState.WaitingForDeadline:
                // Обработка открытия календаря и навигации по нему
                if (message.Text == "Выбрать дату")
                {
                    userState.CalendarMonth = DateTime.Now.Date;
                    await userStateService.UpdateUserState(userId, userState);
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Выберите дату (или введите 'Пропустить' для установки текущей даты):",
                        replyMarkup: BuildCalendarKeyboard(userState.CalendarMonth.Value),
                        cancellationToken: cancellationToken);
                    return;
                }
                if (message.Text == "Отмена")
                {
                    userState.CalendarMonth = null;
                    userState.State = DialogState.WaitingForUrgency;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                // Если календарь открыт — обрабатываем навигацию/выбор
                if (userState.CalendarMonth.HasValue)
                {
                    var month = userState.CalendarMonth.Value;

                    if (message.Text == "<")
                    {
                        userState.CalendarMonth = month.AddMonths(-1);
                        await userStateService.UpdateUserState(userId, userState);
                        await botClient.SendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "Выберите дату (или введите 'Пропустить' для установки текущей даты):",
                            replyMarkup: BuildCalendarKeyboard(userState.CalendarMonth.Value),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    if (message.Text == ">")
                    {
                        userState.CalendarMonth = month.AddMonths(1);
                        await userStateService.UpdateUserState(userId, userState);
                        await botClient.SendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: "Выберите дату (или введите 'Пропустить' для установки текущей даты):",
                            replyMarkup: BuildCalendarKeyboard(userState.CalendarMonth.Value),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    if (message.Text == "Сегодня" || message.Text == "Пропустить")
                    {
                        var selected = DateTime.Now.Date;
                        userState.Application.Deadline = selected.ToString("dd.MM.yyyy");
                        userState.CalendarMonth = null;
                        userState.State = DialogState.WaitingForTime; // Переход к выбору времени
                        userState.PreviousState = DialogState.WaitingForDeadline;
                        await userStateService.UpdateUserState(userId, userState);
                        await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                        return;
                    }
                    if (message.Text == "Отмена")
                    {
                        userState.CalendarMonth = null;
                        userState.State = DialogState.WaitingForUrgency;
                        await userStateService.UpdateUserState(userId, userState);
                        await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                        return;
                    }

                    if (int.TryParse(message.Text, out int day))
                    {
                        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
                        if (day >= 1 && day <= daysInMonth)
                        {
                            var selected = new DateTime(month.Year, month.Month, day);
                            userState.Application.Deadline = selected.ToString("dd.MM.yyyy");
                            userState.CalendarMonth = null;
                            userState.State = DialogState.WaitingForTime; // Переход к выбору времени
                            userState.PreviousState = DialogState.WaitingForDeadline;
                            await userStateService.UpdateUserState(userId, userState);
                            await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                            return;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                                chatId: message.Chat.Id,
                                text: "Неверный день. Пожалуйста, выберите корректный день месяца.",
                                cancellationToken: cancellationToken);
                            return;
                        }
                    }
                }

                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Пожалуйста, выберите дату с помощью календаря.",
                    cancellationToken: cancellationToken);
                break;

            case DialogState.WaitingForTime:
                if (message.Text == "Далее")
                {
                    userState.State = DialogState.WaitingForDescription;
                    userState.PreviousState = DialogState.WaitingForDeadline;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }
                else if (message.Text == "Отмена")
                {
                    userState.State = DialogState.WaitingForDeadline;
                    userState.PreviousState = GetPreviousState(DialogState.WaitingForDeadline); // Возвращаемся на предыдущий этап
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                // Проверяем формат "HH:mm, HH:mm"
                var part = message.Text?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (part?.Length == 2 &&
                    TimeSpan.TryParse(part[0].Trim(), out TimeSpan startTime) &&
                    TimeSpan.TryParse(part[1].Trim(), out TimeSpan endTime))
                {
                    var currentDeadline = DateTime.ParseExact(userState.Application.Deadline, "dd.MM.yyyy", CultureInfo.InvariantCulture);
                    var formattedTime = $"с {startTime:hh\\:mm} до {endTime:hh\\:mm}";
                    userState.Application.Deadline = $"{currentDeadline:dd.MM.yyyy} {formattedTime}";
                    userState.State = DialogState.WaitingForDescription;
                    userState.PreviousState = DialogState.WaitingForDeadline;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Введите время в формате 'HH:mm, HH:mm' (например, '15:00, 18:00') или 'Далее' для продолжения без времени.",
                        cancellationToken: cancellationToken);
                    return;
                }

            case DialogState.WaitingForDescription:
                userState.Application.Description = message.Text;
                userState.State = DialogState.WaitingForPhoto;
                userState.PreviousState = DialogState.WaitingForDescription;
                userState.MediaFileIds = new List<string>();
                userState.Application.MediaFileIds = new List<string>();
                await userStateService.UpdateUserState(userId, userState);
                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                break;

            case DialogState.WaitingForPhoto:
                var photoKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("Назад"), new KeyboardButton("Далее") }
                })
                {
                    ResizeKeyboard = true
                };

                // 📌 Если пришло фото
                if (message.Photo != null && message.Photo.Length > 0)
                {
                    var largestPhoto = message.Photo.Last();
                    var fileId = largestPhoto.FileId;

                    if (!userState.MediaFileIds.Contains(fileId))
                    {
                        userState.MediaFileIds.Add(fileId);
                        await userStateService.UpdateUserState(userId, userState);
                    }

                    if (string.IsNullOrEmpty(message.MediaGroupId))
                    {
                        await botClient.SendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: $"📸 Медиафайл добавлен.  Всего:  {userState.MediaFileIds.Count} шт.  " +
                                  "Прикрепите ещё или нажмите 'Далее'.",
                            replyMarkup: photoKeyboard,
                            cancellationToken: cancellationToken);
                    }
                }
                // 📌 Если пришло видео
                else if (message.Video != null)
                {
                    var fileId = message.Video.FileId;

                    if (!userState.MediaFileIds.Contains(fileId))
                    {
                        userState.MediaFileIds.Add(fileId);
                        await userStateService.UpdateUserState(userId, userState);
                    }

                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: $"🎥 Медиафайл добавлен. Всего: {userState.MediaFileIds.Count} шт. " +
                              "Прикрепите ещё или нажмите 'Далее'.",
                        replyMarkup: photoKeyboard,
                        cancellationToken: cancellationToken);
                }
                // 📌 Если текст — обработка кнопок
                else if (message.Text == "Далее")
                {
                    // Копируем список — избегаем shared reference
                    userState.Application.MediaFileIds = userState.MediaFileIds?.Distinct().ToList() ?? new List<string>();

                    userState.State = DialogState.WaitingForContact;
                    userState.PreviousState = DialogState.WaitingForPhoto;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                }
                else
                {
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Пожалуйста, прикрепите фото или видео, или нажмите 'Далее'.",
                        replyMarkup: photoKeyboard,
                        cancellationToken: cancellationToken);
                }
                break;

            case DialogState.WaitingForContact:
                try
                {
                    var contactParts = message.Text.Split(new[] { ' ' }, 2);
                    userState.Application.ContactName = contactParts[0].Trim();
                    userState.Application.ContactPhone = contactParts.Length > 1 ? contactParts[1].Trim() : "";
                    userState.Application.TelegramUserId = message.From.Id;
                    userState.Application.Status = "Открыто";
                    userState.State = DialogState.None;

                    // Обязательная копия перед отправкой в Sheets
                    userState.Application.MediaFileIds = userState.Application.MediaFileIds?.Distinct().ToList() ?? new List<string>();

                    await userStateService.UpdateUserState(userId, userState);


                    var chatMapping = chatMappings?.FirstOrDefault(cm =>
                        cm.Object == userState.Application.Object && cm.Direction == userState.Application.Direction);

                    if (chatMapping != null)
                    {
                        var targetChatId = chatMapping.ChatId;

                        var (rowId, mediaUrl) = await googleSheetsService.AppendApplicationAsync(userState.Application, botClient, targetChatId);

                        var mediaInfo = userState.Application.MediaFileIds.Any() ? $"с {userState.Application.MediaFileIds.Count} медиафайлами" : "без медиафайлов";

                        Console.WriteLine($"Новая заявка:  {userState.Application.Object} | {userState.Application.Direction} | {userState.Application.Location} | {userState.Application.Urgency}.. .");

                        var messageText = $"Новая заявка:\n" +
                                         $"🆔 ID заявки: {rowId}\n" +
                                         $"🏢 Объект: {userState.Application.Object}\n" +
                                         $"📍 Местонахождение: {userState.Application.Location}\n" +
                                         $"🔧 Направление: {userState.Application.Direction}\n" +
                                         $"⚡ Приоритет: {userState.Application.Urgency}\n" +
                                         (string.IsNullOrEmpty(userState.Application.Deadline) ? "\n" : $"📅 Желаемая дата и время: ({userState.Application.Deadline})\n") +
                                         $"📝 Описание: {userState.Application.Description}\n" +
                                         $"👤 ФИО и контакт: {userState.Application.ContactName} {userState.Application.ContactPhone}\n" +
                                         $"📎 Медиафайлы: {(userState.Application.MediaFileIds.Any() ? userState.Application.MediaFileIds.Count : 0)} шт.";


                        var sentMessage = await botClient.SendTextMessageAsync(
                            chatId: targetChatId,
                            parseMode: ParseMode.Html,
                            text: messageText,
                            cancellationToken: cancellationToken);

                        await tgMessageService.SaveMessageAsync(rowId, targetChatId, sentMessage.MessageId);

                        // отправка медиа: используем копию списка (чтобы дальнейшие изменения userState не влияли)
                        var mediaToSend = new List<string>(userState.Application.MediaFileIds ?? new List<string>());
                        foreach (var mediaId in mediaToSend)
                        {
                            try
                            {
                                var file = await botClient.GetFileAsync(mediaId);
                                if (file.FilePath?.Contains("video") == true)
                                    await botClient.SendVideoAsync(chatId: targetChatId, video: InputFile.FromFileId(mediaId), caption: "Видео к заявке", cancellationToken: cancellationToken);
                                else
                                    await botClient.SendPhotoAsync(chatId: targetChatId, photo: InputFile.FromFileId(mediaId), caption: "Фото к заявке", cancellationToken: cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Ошибка при отправке медиа {MediaId}", mediaId);
                            }
                        }

                        Log.Information("Сообщение успешно отправлено в чат:  {ChatId}", targetChatId);

                        var directionalSheetsService = host.Services.GetRequiredService<DirectionalSheetsService>();
                        var userDatabaseService = host.Services.GetRequiredService<UserDatabaseService>();

                        if (directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                        {
                            var (success, dirRowId, dirMessage) = await directionalSheetsService
                                .AppendApplicationToDirectionSheetAsync(
                                    userState.Application,
                                    userState.Application.Direction,
                                    botClient);

                            if (success)
                            {
                                // ✅ СОХРАНЯЕМ МАППИНГ В БД
                                try
                                {
                                    var config = directionalSheetsService.GetDirectionConfig(userState.Application.Direction);
                                    if (config != null)
                                    {
                                        await userDatabaseService.SaveApplicationMappingAsync(
                                            mainRowId: rowId,
                                            directionRowId: dirRowId,
                                            direction: userState.Application.Direction,
                                            directionSpreadsheetId: config.SpreadsheetId,
                                            directionSheetName: config.SheetName);

                                        Log.Information(
                                            "Маппинг сохранён в БД: Main={MainRowId}, Direction={DirectionRowId}, Type={Direction}",
                                            rowId, dirRowId, userState.Application.Direction);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning(ex, "Ошибка при сохранении маппинга в БД");
                                }

                                Log.Information("Заявка также записана в отдельную таблицу направления. RowId={RowId}", dirRowId);

                            }
                            else
                            {
                                Log.Warning("Не удалось записать в отдельную таблицу: {Message}", dirMessage);
                            }
                        }

                        await botClient.SendTextMessageAsync(
                            chatId: message.Chat.Id,
                            text: $"✅ Заявка #{rowId} успешно создана и записана!  Спасибо.",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка при записи в Google Sheets или маршрутизации");
                    await botClient.SendTextMessageAsync(
                        chatId: message.Chat.Id,
                        text: "Ошибка при записи заявки. Попробуйте позже.",
                        replyMarkup: new ReplyKeyboardRemove(),
                        cancellationToken: cancellationToken);
                }
                finally
                {

                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                }
                break;

            
        }
    }

    private static bool TryParseRowId(string? text, out int rowId)
    {
        rowId = 0;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("#"))
            return false;

        // Извлекаем ТОЛЬКО число после "#", игнорируя остаток строки
        var match = System.Text.RegularExpressions.Regex.Match(text, @"#(\d+)");
        if (!match.Success)
            return false;

        return int.TryParse(match.Groups[1].Value, out rowId);
    }


    static async Task ProcessEditMessage(IHost host, ITelegramBotClient botClient, Message message, UserState userState, UserStateService userStateService, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var userId = message.From.Id;
        var googleSheetsService = host.Services.GetRequiredService<GoogleSheetsService>();
        var directionalSheetsService = host.Services.GetRequiredService<DirectionalSheetsService>();
        var userDatabaseService = host.Services.GetRequiredService<UserDatabaseService>();
        var tgMessageService = host.Services.GetRequiredService<TelegramMessageService>();
        var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>();

        var chatType = message.Chat.Type;
        if (chatType != ChatType.Private)
        {
            Log.Information("Игнорирую сообщение из группового чата: ChatType={ChatType}", chatType);
            return; 
        }

        string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\p{C}+", "");  // Удаляем control chars
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ");  // Сжимаем пробелы
            return s;
        }

        var input = Normalize(message.Text);

        if (message.Text == "Назад")
        {
            if (userState.PreviousState != DialogState.None)
            {
                userState.State = userState.PreviousState;
                userState.PreviousState = GetPreviousState(userState.State);
                await userStateService.UpdateUserState(userId, userState);
                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId: message.Chat.Id,
                    text: "Нет предыдущего шага.",
                    replyMarkup: new ReplyKeyboardRemove(),
                    cancellationToken: cancellationToken);
            }
            return;
        }

        switch (userState.State)
        {
            case DialogState.WaitingForEditSelection:
                if (input == "Отмена")
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Редактирование отменено.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    await userStateService.ClearUserState(userId, null);
                    return;
                }

                if (!TryParseRowId(input, out var rowIds))
                {
                    Log.Warning("WaitingForEditSelection: unexpected input '{Text}' from user {UserId}", message.Text, userId);
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Пожалуйста, выберите заявку кнопкой ниже.", cancellationToken: cancellationToken);
                    return;
                }

                var app = await googleSheetsService.GetApplicationByRowIdAsync(rowIds);
                var tgMsgt = await tgMessageService.GetMessageAsync(rowIds);

                if (app == null || tgMsgt == null)
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Заявка не найдена.");
                    return;
                }

                var currentAppMessage = ViewApplicationMessage(app);
                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    $"Текущий вид заявки #{rowIds}:\n\n{currentAppMessage}",
                    parseMode: ParseMode.Html,
                    cancellationToken: cancellationToken);

                userState.Application = app;
                userState.Application.RowId = rowIds;
                userState.State = DialogState.ChoosingEditField;
                await userStateService.UpdateUserState(userId, userState);

                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);

                break;

            case DialogState.ChoosingEditField:
                if (input == "Отмена")
                {
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        "Редактирование отменено.",
                        replyMarkup: new ReplyKeyboardRemove(),
                        cancellationToken: cancellationToken);

                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    return;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        "Пожалуйста, выберите поле с клавиатуры.",
                        cancellationToken: cancellationToken);
                    return;
                }

                var fieldMap = new Dictionary<string, string>
                {
                    ["Местоположение"] = "location",
                    ["Срочность/Дедлайн"] = "urgency",
                    ["Описание"] = "description",
                    ["Контакт"] = "contact"
                };

                if (!fieldMap.TryGetValue(input, out var field))
                {
                    // Гибкий матч (если есть эмодзи или опечатки)
                    field = fieldMap.Keys.FirstOrDefault(k => input.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                }

                if (string.IsNullOrEmpty(field))
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Выберите поле с клавиатуры.", cancellationToken: cancellationToken);
                    return;
                }

                userState.EditingField = field;
                await userStateService.UpdateUserState(userId, userState);

                if (field == "urgency")
                {
                    userState.State = DialogState.EditingUrgencyPick;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    $"Введите новое значение для <b>{message.Text}</b>:",
                    parseMode: ParseMode.Html,
                    replyMarkup: new ReplyKeyboardMarkup(new KeyboardButton("Отмена")) { ResizeKeyboard = true },
                    cancellationToken: cancellationToken);

                userState.State = DialogState.EditingField;
                break;

            case DialogState.EditingUrgencyPick:
                var texts = (message.Text ?? "").Trim();

                if (texts == "Отмена")
                {
                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Отменено.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return;
                }

                if (texts == "🔴 НЕМЕДЛЕННО (выполнение до 30 мин)" || texts == "\U0001f7e1 СРОЧНО (выполнение до 2-4 часов)" || texts == "\U0001f7e2 В РАБОЧИЙ ДЕНЬ (выполнение до 8-12 часов)" || texts == "🔵 ПЛАНОВЫЙ (выполнение 3-5 дней)")
                {
                    userState.Application.Urgency = texts;
                    await userStateService.UpdateUserState(userId, userState);

                    var currentDeadline = string.IsNullOrEmpty(userState.Application.Deadline) ? "не указан" : userState.Application.Deadline;
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        $"Приоритет установлен, текущий дедлайн: {currentDeadline}. При желании — выберите новую дату дедлайна или нажмите 'Готово'.",
                        replyMarkup: new ReplyKeyboardMarkup(new[]
                        {
                            new[] { new KeyboardButton("Выбрать дату"), new KeyboardButton("Готово") },
                            new[] { new KeyboardButton("Отмена") }
                        })
                        { ResizeKeyboard = true },
                        cancellationToken: cancellationToken);

                    return;
                }

                if (texts == "Выбрать дату")
                {
                    userState.CalendarMonth = DateTime.Now;
                    userState.State = DialogState.EditingDeadlinePick;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                if (texts == "Готово")
                {
                    await googleSheetsService.UpdateEditableFieldsAsync(userState.Application);

                    // ✅ Обновляем в отдельной таблице (если она есть)
                    if (!string.IsNullOrEmpty(userState.Application?.Direction) &&
                        directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                    {
                        await directionalSheetsService.UpdateEditableFieldsInDirectionSheetAsync(
                            userState.Application,
                            userState.Application.Direction);

                        Log.Information(
                            "Приоритет также обновлён в отдельной таблице для направления '{Direction}'",
                            userState.Application.Direction);
                    }

                    var tgMsgs = await tgMessageService.GetMessageAsync(userState.Application.RowId);
                    if (tgMsgs != null)
                    {
                        var newText = BuildApplicationMessage(userState.Application);
                        try
                        {
                            await botClient.EditMessageTextAsync(tgMsgs.ChatId, tgMsgs.MessageId, newText, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                        }
                        catch
                        {
                            // игнор
                        }
                    }

                    await botClient.SendTextMessageAsync(message.Chat.Id, $" Приоритет для заявки #{userState.Application.RowId} обновлен.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    return;
                }

                await botClient.SendTextMessageAsync(message.Chat.Id, "Пожалуйста, используйте клавиатуру для выбора приоритета/даты.", cancellationToken: cancellationToken);
                return;

            case DialogState.EditingDeadlinePick:
                if (message.Text == "Сегодня")
                {
                    var today = DateTime.Today;

                    userState.CalendarMonth = today;
                    userState.Application.Deadline = today.ToString("dd.MM.yyyy");
                    userState.State = DialogState.EditingTimePick;

                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                if (message.Text == "Отмена")
                {
                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Отменено.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return;
                }

                // Навигация календарём
                if (message.Text == "<")
                {
                    userState.CalendarMonth = userState.CalendarMonth?.AddMonths(-1) ?? DateTime.Now.AddMonths(-1);
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }
                if (message.Text == ">")
                {
                    userState.CalendarMonth = userState.CalendarMonth?.AddMonths(1) ?? DateTime.Now.AddMonths(1);
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                // Выбор дня
                if (int.TryParse(message.Text, out int sendDay))
                {
                    if (userState.CalendarMonth == null)
                        userState.CalendarMonth = DateTime.Now;

                    var selectedDate = new DateTime(userState.CalendarMonth.Value.Year, userState.CalendarMonth.Value.Month, Math.Min(sendDay, DateTime.DaysInMonth(userState.CalendarMonth.Value.Year, userState.CalendarMonth.Value.Month)));
                    userState.Application.Deadline = selectedDate.ToString("dd.MM.yyyy");
                    userState.State = DialogState.EditingTimePick;
                    await userStateService.UpdateUserState(userId, userState);
                    await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                    return;
                }

                await ShowCurrentStep(botClient, userState, userId, configuration, cancellationToken, googleSheetsService, userStateService);
                return;

            case DialogState.EditingTimePick:
                var txt = (message.Text ?? "").Trim();

                if (txt == "Готово")
                {
                    await googleSheetsService.UpdateEditableFieldsAsync(userState.Application);

                    // ✅ Обновляем в отдельной таблице (если она есть)
                    if (!string.IsNullOrEmpty(userState.Application?.Direction) &&
                        directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                    {
                        await directionalSheetsService.UpdateEditableFieldsInDirectionSheetAsync(
                            userState.Application,
                            userState.Application.Direction);

                        Log.Information(
                            "Дедлайн также обновлён в отдельной таблице для направления '{Direction}'",
                            userState.Application.Direction);
                    }

                    await TryEditTelegramMessage(botClient, tgMessageService, userState, cancellationToken);

                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        $" Заявка #{userState.Application.RowId} обновлена.",
                        replyMarkup: new ReplyKeyboardRemove(),
                        cancellationToken: cancellationToken);

                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    return;
                }

                if (txt == "Отмена")
                {
                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Отменено.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return;
                }

                // Проверяем формат "с HH:mm до HH:mm"
                var partsed = txt.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (partsed.Length == 2 &&
                    TimeSpan.TryParse(partsed[0].Trim(), out TimeSpan startTimes) &&
                    TimeSpan.TryParse(partsed[1].Trim(), out TimeSpan endTimes))
                {
                    // append range to previously selected date
                    var formattedTime = $"с {startTimes:hh\\:mm} до {endTimes:hh\\:mm}";

                    if (!string.IsNullOrWhiteSpace(userState.Application.Deadline))
                    {
                        userState.Application.Deadline = $"{userState.Application.Deadline} {formattedTime}";
                    }
                    else
                    {
                        userState.Application.Deadline = DateTime.Today.ToString("dd.MM.yyyy") + $" {formattedTime}";
                    }

                    await userStateService.UpdateUserState(userId, userState);

                    await googleSheetsService.UpdateEditableFieldsAsync(userState.Application);

                    if (!string.IsNullOrEmpty(userState.Application?.Direction) && directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                    {
                        await directionalSheetsService.UpdateEditableFieldsInDirectionSheetAsync(
                            userState.Application,
                            userState.Application.Direction);

                        Log.Information(
                            "Дедлайн с временем также обновлён в отдельной таблице для направления '{Direction}'",
                            userState.Application.Direction);
                    }
                    await TryEditTelegramMessage(botClient, tgMessageService, userState, cancellationToken);

                    await botClient.SendTextMessageAsync(message.Chat.Id, $" Дедлайн и приоритет для заявки #{userState.Application.RowId} сохранены.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    return;
                }

                await botClient.SendTextMessageAsync(message.Chat.Id, "Неверный формат времени. Введите время в формате 'HH:mm , HH:mm' (например, '15:00, 18:00').", cancellationToken: cancellationToken);
                return;

            case DialogState.EditingField:
                if (message.Text == "Отмена")
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Редактирование отменено.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    await userStateService.ClearUserState(userId, userState.Application.RowId);
                    return;
                }

                var text = message.Text.Trim();

                switch (userState.EditingField)
                {

                    case "location":
                        userState.Application.Location = text;
                        break;

                    case "urgency":
                        if (text.Contains("+"))
                        {
                            userState.Application.Urgency = "Срочно";
                            userState.Application.Deadline = DateTime.Now.AddHours(2).ToString("dd.MM.yyyy HH:mm");
                        }
                        else
                        {
                            userState.Application.Urgency = text;
                        }
                        break;

                    case "description":
                        userState.Application.Description = text;
                        break;

                    case "contact":
                        var parts = text.Split(' ', 2);
                        userState.Application.ContactName = parts[0];
                        userState.Application.ContactPhone = parts.Length > 1 ? parts[1] : "";
                        break;
                }

                await googleSheetsService.UpdateEditableFieldsAsync(userState.Application);

                
                if (!string.IsNullOrEmpty(userState.Application?.Direction) &&
                    directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                {
                    try
                    {
                        // ✅ ПОЛУЧАЕМ ПРАВИЛЬНЫЙ ROW ID ИЗ БД
                        var mapping = userDatabaseService.GetApplicationMapping(
                            userState.Application.RowId,
                            userState.Application.Direction);

                        if (mapping != null)
                        {
                            // Обновляем по ID из отдельной таблицы
                            var directionApp = new Application
                            {
                                RowId = mapping.DirectionRowId,
                                Location = userState.Application.Location,
                                Description = userState.Application.Description,
                                ContactName = userState.Application.ContactName,
                                ContactPhone = userState.Application.ContactPhone,
                                Urgency = userState.Application.Urgency,
                                Deadline = userState.Application.Deadline
                            };

                            await directionalSheetsService.UpdateEditableFieldsInDirectionSheetAsync(
                                directionApp,
                                userState.Application.Direction);

                            Log.Information(
                                "Данные обновлены в отдельной таблице (DirectionRowId={DirectionRowId}) для направления '{Direction}'",
                                mapping.DirectionRowId, userState.Application.Direction);
                        }
                        else
                        {
                            Log.Debug(
                                "Маппинг не найден для MainRowId={MainRowId}, Direction={Direction}",
                                userState.Application.RowId, userState.Application.Direction);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex,
                            "Ошибка при обновлении редактируемых полей в отдельной таблице для направления '{Direction}'",
                            userState.Application.Direction);
                    }
                }

                var tgMsg = await tgMessageService.GetMessageAsync(userState.Application.RowId);
                if (tgMsg != null)
                {
                    var newText = BuildApplicationMessage(userState.Application);
                    await botClient.EditMessageTextAsync(
                        chatId: tgMsg.ChatId,
                        messageId: tgMsg.MessageId,
                        text: newText,
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);
                }

                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    $" Заявка #{userState.Application.RowId} обновлена!",
                    replyMarkup: new ReplyKeyboardRemove(),
                    cancellationToken: cancellationToken);

                await userStateService.ClearUserState(userId, userState.Application.RowId);
                break;
        }
    }

    private static async Task TryEditTelegramMessage(
    ITelegramBotClient botClient,
    TelegramMessageService tgService,
    UserState userState,
    CancellationToken cancellationToken)
    {
        var tgMsg = await tgService.GetMessageAsync(userState.Application.RowId);
        if (tgMsg == null) return;

        var text = BuildApplicationMessage(userState.Application);

        await botClient.EditMessageTextAsync(
            tgMsg.ChatId,
            tgMsg.MessageId,
            text,
            parseMode: ParseMode.Html,
            cancellationToken: cancellationToken);
    }

    static async Task<bool> TrySendAsync(
    Func<CancellationToken, Task> sendFunc,
    long userId,
    DialogState state,
    CancellationToken appCancellationToken,
    IAdaptiveBackoffService backoffService,
    int maxAttempts = 3,
    TimeSpan? perAttemptTimeout = null)
{
    perAttemptTimeout ??= TimeSpan.FromSeconds(15);
    var key = $"send_{userId}_{state}";

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        if (appCancellationToken.IsCancellationRequested)
        {
            Log.Information("Отправка прервана (приложение завершается). UserId={UserId}, State={State}", userId, state);
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCancellationToken);
        cts.CancelAfter(perAttemptTimeout.Value);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await sendFunc(cts.Token).ConfigureAwait(false);
            sw.Stop();

            backoffService.ResetBackoff(key);
            Log.Debug("Сообщение отправлено успешно за {DurationMs}ms. UserId={UserId}", sw.ElapsedMilliseconds, userId);
            return true;
        }
        catch (OperationCanceledException oce) when (!appCancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            Log.Warning(
                "Попытка {Attempt}/{Max} истекла по времени ({DurationMs}ms). UserId={UserId}, State={State}",
                attempt, maxAttempts, sw.ElapsedMilliseconds, userId, state);

            if (attempt < maxAttempts)
            {
                var delay = await backoffService.GetBackoffDelayAsync(key, attempt, oce);
                await Task.Delay(delay, appCancellationToken).ConfigureAwait(false);
            }
        }
        catch (ApiRequestException apiEx) when (apiEx.ErrorCode == 429)
        {
            sw.Stop();
            var wait = apiEx.Parameters?.RetryAfter ?? 30;
            Log.Warning(
                "Rate limit (429). Ожидание {Wait}s. UserId={UserId}, Попытка={Attempt}/{Max}",
                wait, userId, attempt, maxAttempts);

            if (attempt < maxAttempts)
            {
                var delay = await backoffService.GetBackoffDelayAsync(key, attempt, apiEx);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(wait, (int)delay.TotalSeconds)), appCancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.BadGateway || 
                                                   httpEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                                                   httpEx.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
        {
            sw.Stop();
            Log.Warning(
                httpEx,
                "Ошибка сети {StatusCode}. UserId={UserId}, Попытка={Attempt}/{Max}, Длительность={DurationMs}ms",
                httpEx.StatusCode, userId, attempt, maxAttempts, sw.ElapsedMilliseconds);

            if (attempt < maxAttempts)
            {
                var delay = await backoffService.GetBackoffDelayAsync(key, attempt, httpEx);
                await Task.Delay(delay, appCancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Warning(
                ex,
                "Ошибка при отправке. UserId={UserId}, Попытка={Attempt}/{Max}, Длительность={DurationMs}ms",
                userId, attempt, maxAttempts, sw.ElapsedMilliseconds);

            if (attempt < maxAttempts)
            {
                var delay = await backoffService.GetBackoffDelayAsync(key, attempt, ex);
                await Task.Delay(delay, appCancellationToken).ConfigureAwait(false);
            }
        }
    }

    Log.Error("Критическая ошибка: не удалось отправить сообщение после {MaxAttempts} попыток. UserId={UserId}, State={State}", 
        maxAttempts, userId, state);
    return false;
}

    static async Task<bool> ShowStepAsync(ITelegramBotClient botClient, UserState userState, long userId, CancellationToken ct, UserStateService userStateService, Func<CancellationToken, Task> sendFunc, IAdaptiveBackoffService backoffService)
    {
        // Попытка основного сообщения
        var ok = await TrySendAsync(sendFunc, userId, userState.State, ct, backoffService);

        if (ok)
        {
            userState.LastShownState = userState.State;
            await userStateService.UpdateUserState(userId, userState);
        }
        else
        {
            Log.Information("Не удалось показать шаг для user {UserId} (state={State}). Сетевая/временная ошибка — оставляю текущее состояние без изменений.", userId, userState.State);
            return false;
        }

        return ok;
    }


    static async Task<bool> ShowCurrentStep(ITelegramBotClient botClient, UserState userState, long userId, IConfiguration configuration, CancellationToken cancellationToken, GoogleSheetsService sheetsService, UserStateService userStateService, IAdaptiveBackoffService backoffService)
    {
        var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>();
 
        if (userState.LastShownState == userState.State)
        {
            Serilog.Log.Debug(
                "Skip ShowCurrentStep: state {State} already shown for user {UserId}",
                userState.State,
                userId);
            return false;
        }

        switch (userState.State)
        {
            case DialogState.WaitingForObject:
                {
                    var objects = chatMappings?.Select(cm => cm.Object).Distinct().ToList() ?? new();
                    if (!objects.Any())
                    {
                        await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService, backoffService,
                            ct => botClient.SendTextMessageAsync(
                                chatId: userId,
                                text: "Нет доступных объектов. Обратитесь к администратору.",
                                replyMarkup: new ReplyKeyboardRemove(),
                                cancellationToken: ct));
                        break;
                    }

                    var rows = new List<KeyboardButton[]>();
                    for (int i = 0; i < objects.Count; i += 2)
                        rows.Add(objects.Skip(i).Take(2).Select(o => new KeyboardButton(o)).ToArray());

                    rows.Add(new[] { new KeyboardButton("Отмена") });

                    var keyboard = new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };

                    await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService, 
                        ct => botClient.SendTextMessageAsync(
                            chatId: userId,
                            text: "Выберите объект:",
                            replyMarkup: keyboard,
                            cancellationToken: ct));
                    break;
                }


            case DialogState.WaitingForDirectionCategory:
                {
                    var categories = GetAvailableCategoryNamesForObject(configuration, chatMappings, userState.Application.Object);
                    if (!categories.Any())
                    {
                        await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                            ct => botClient.SendTextMessageAsync(
                                chatId: userId,
                                text: "Нет доступных категорий для выбранного объекта.",
                                replyMarkup: new ReplyKeyboardRemove(),
                                cancellationToken: ct));
                        break;
                    }

                    var rows = new List<KeyboardButton[]>();
                    for (int i = 0; i < categories.Count; i += 2)
                        rows.Add(categories.Skip(i).Take(2).Select(c => new KeyboardButton(c)).ToArray());

                    rows.Add(new[] { new KeyboardButton("Назад") });

                    var keyboard = new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };

                    await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                            chatId: userId,
                            text: "Выберите категорию направления работ:",
                            replyMarkup: keyboard,
                            cancellationToken: ct));
                    break;
                }


            case DialogState.WaitingForDirection:
                var availableDirectionDisplayNames = GetAvailableDirectionsByCategory(
                    configuration,
                    chatMappings,
                    userState.Application.Object,
                    userState.SelectedDirectionCategory);

                if (!availableDirectionDisplayNames.Any())
                {
                    await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                    ct => botClient.SendTextMessageAsync(
                        chatId: userId,
                        text: "Нет доступных направлений для выбранной категории. Обратитесь к администратору.",
                        replyMarkup: new ReplyKeyboardRemove(),
                        cancellationToken: ct));
                    break;

                }

                var directionRows = availableDirectionDisplayNames.Select(d => new[] { new KeyboardButton(d) }).ToList();

                directionRows.Add(new[] { new KeyboardButton("Назад") });

                var keyboardDirections = new ReplyKeyboardMarkup(directionRows)
                {
                    ResizeKeyboard = true
                };

                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                    ct => botClient.SendTextMessageAsync(
                        chatId: userId,
                        text: "Выберите направление работ:",
                        replyMarkup: keyboardDirections,
                        cancellationToken: ct));
                
            break;

            case DialogState.WaitingForLocation:
                var keyboardLocation = new ReplyKeyboardMarkup(new[] { new KeyboardButton("Назад") })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                    ct => botClient.SendTextMessageAsync(
                        chatId: userId,
                        text: "Уточните местонахождение (например: номер помещения и этаж):",
                        replyMarkup: keyboardLocation,
                        cancellationToken: ct));
                break;

            case DialogState.WaitingForUrgency:
                var keyboardUrgency = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("🔴 НЕМЕДЛЕННО (выполнение до 30 мин)"), new KeyboardButton("\U0001f7e1 СРОЧНО (выполнение до 2-4 часов)") },
                    new[] { new KeyboardButton("\U0001f7e2 В РАБОЧИЙ ДЕНЬ (выполнение до 8-12 часов)"), new KeyboardButton("🔵 ПЛАНОВЫЙ (выполнение 3-5 дней)") },
                    new[] { new KeyboardButton("Назад") }
                })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Выберите приоритет:",
                    replyMarkup: keyboardUrgency,
                    cancellationToken: ct));
                break;

            case DialogState.WaitingForDeadline:
                var keyboardDeadline = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("Отмена"), new KeyboardButton("Выбрать дату") }
                })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Укажите дедлайн (дату выполнения):",
                    replyMarkup: keyboardDeadline,
                    cancellationToken: ct));
                break;

            case DialogState.WaitingForTime:
                var keyboardTime = new ReplyKeyboardMarkup(new[]
                {
                    new[] {new KeyboardButton("Отмена"), new KeyboardButton("Далее") }
                })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Введите время с какого до какого в формате 'HH:mm, HH:mm' (например, '15:00, 18:00') или 'Далее' для продолжения без времени.",
                    replyMarkup: keyboardTime,
                    cancellationToken: ct));
                break;

            case DialogState.WaitingForDescription:
                var keyboard3 = new ReplyKeyboardMarkup(new[] { new KeyboardButton("Назад") })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Опишите проблему (что сломалось, где и т.д.):",
                    replyMarkup: keyboard3,
                    cancellationToken: ct));
                break;

            case DialogState.WaitingForPhoto:
                var keyboard4 = new ReplyKeyboardMarkup(new[]
                {
                    new[] {new KeyboardButton("Назад"), new KeyboardButton("Далее") }
                })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Прикрепите фото (можно несколько, нажмите 'Далее' после завершения):",
                    replyMarkup: keyboard4,
                    cancellationToken: ct));
                break;

            case DialogState.WaitingForContact:
                var keyboard5 = new ReplyKeyboardMarkup(new[] { new KeyboardButton("Назад") })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Укажите свои каонтактные данные (ФИО и номер телефона):",
                    replyMarkup: keyboard5,
                    cancellationToken: ct));
                break;

            // ДОБАВЛЕННЫЕ CASE ДЛЯ РЕДАКТИРОВАНИЯ
            case DialogState.WaitingForEditSelection:
                var openApps = await sheetsService.GetOpenApplicationsAsync(); 
                var editSelectionButtons = openApps.Select(a => new KeyboardButton($"#{a.RowId} | {a.Object} | {a.Direction}")).ToArray();
                var keyboardEditSelection = new ReplyKeyboardMarkup(editSelectionButtons.Concat(new[] { new KeyboardButton("Отмена") }).ToArray())
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Выберите заявку для редактирования:",
                    replyMarkup: keyboardEditSelection,
                    cancellationToken: ct));
                break;

            case DialogState.ChoosingEditField:
                var editKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("Местоположение"), new KeyboardButton("Срочность/Дедлайн") },
                    new[] { new KeyboardButton("Описание"), new KeyboardButton("Контакт") },
                    new[] { new KeyboardButton("Отмена") }
                })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: $"Редактирование заявки <b>#{userState.Application.RowId}</b>\nВыберите поле:",
                    parseMode: ParseMode.Html,
                    replyMarkup: editKeyboard,
                    cancellationToken: ct));
                break;

            case DialogState.EditingUrgencyPick:
                var keyboardUrgencyEdit = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("🔴 НЕМЕДЛЕННО (выполнение до 30 мин)"), new KeyboardButton("\U0001f7e1 СРОЧНО (выполнение до 2-4 часов)") },
                    new[] { new KeyboardButton("\U0001f7e2 В РАБОЧИЙ ДЕНЬ (выполнение до 8-12 часов)"), new KeyboardButton("🔵 ПЛАНОВЫЙ (выполнение 3-5 дней)") },
                    new[] { new KeyboardButton("Отмена") }
                })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Выберите срочность:",
                    replyMarkup: keyboardUrgencyEdit,
                    cancellationToken: ct));
                break;

            case DialogState.EditingDeadlinePick:
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService,
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Выберите дату дедлайна:",
                    replyMarkup: BuildCalendarKeyboard(userState.CalendarMonth ?? DateTime.Now),
                    cancellationToken: ct));
                break;

            case DialogState.EditingTimePick:
                var keyboardTimeEdit = new ReplyKeyboardMarkup(new[]
                {
                new[] { new KeyboardButton("Готово"), new KeyboardButton("Отмена") }
            })
                {
                    ResizeKeyboard = true
                };
                await ShowStepAsync(botClient, userState, userId, cancellationToken, userStateService, 
                        ct => botClient.SendTextMessageAsync(
                    chatId: userId,
                    text: "Введите время с какого до какого в формате 'HH:mm , HH:mm' (например, '15:00, 18:00') или нажмите 'Готово':",
                    replyMarkup: keyboardTimeEdit,
                    cancellationToken: ct));
                break;
        }
        return true;
    }

    static async Task ProcessClosePhoto(IHost host, ITelegramBotClient botClient, Message message, UserState userState,
    UserStateService userStateService, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var userId = message.From?.Id ?? 0;
        if (userId == 0) return; // игнорируем системные/бот-сообщения

        var googleSheetsService = host.Services.GetRequiredService<GoogleSheetsService>();

        // Получаем актуальный state из сервиса (чтобы работать с одним экземпляром)
        var cur = await userStateService.GetUserState(userId) ?? userState;
        userState = cur; // работаем с cur дальше

        // Удобная функция для чтения rowId
        int GetCurrentRowId() => userState.RowId.GetValueOrDefault(0);

        var keyboard = new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("Назад"), new KeyboardButton("Далее") } })
        {
            ResizeKeyboard = true
        };

        userState.ClosureMediaFileIds ??= new List<string>();

        // Переменные для creator/status — используем userState.OriginalCreatorId, чтобы не перезапрашивать
        long creatorId = userState.OriginalCreatorId.GetValueOrDefault(0);
        string currentStatus = "";

        bool isSuccessfulClose = false; // Флаг успеха для finally

        try
        {
            switch (userState.State)
            {
                case DialogState.WaitingForCloseRowId:
                    {
                        if (string.Equals(message.Text, "Отмена", StringComparison.OrdinalIgnoreCase))
                        {
                            userState.State = DialogState.None;
                            await userStateService.UpdateUserState(userId, userState);
                            await botClient.SendTextMessageAsync(message.Chat.Id, "Операция отменена.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                            return;
                        }

                        if (!int.TryParse(message.Text ?? "", out int parsedRowId) || parsedRowId < 2)
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id, "❌ Неверный ID заявки. Укажите число, начиная с 2.", cancellationToken: cancellationToken);
                            return;
                        }

                        // НОВОЕ: Проверяем максимальный ID
                        int maxRowId = await googleSheetsService.GetMaxRowIdAsync();
                        if (parsedRowId > maxRowId)
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                $"❌ ID {parsedRowId} превышает максимальный ID заявки ({maxRowId}). " +
                                $"Пожалуйста, введите корректный ID.",
                                cancellationToken: cancellationToken);
                            return;
                        }

                        // НОВОЕ: Проверяем, что заявка с таким ID существует
                        var existingApp = await googleSheetsService.GetApplicationByRowIdAsync(parsedRowId);
                        if (existingApp == null)
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                $"❌ Заявка с ID {parsedRowId} не найдена. " +
                                $"Проверьте корректность ID.",
                                cancellationToken: cancellationToken);
                            return;
                        }

                        // Обновляем текущий state (НЕ создаём новый отдельный объект)
                        userState.RowId = parsedRowId;
                        userState.Application = existingApp;
                        userState.Application.RowId = parsedRowId;
                        userState.ClosureMediaFileIds = new List<string>();
                        userState.State = DialogState.WaitingForClosePhoto;
                        userState.OriginalCreatorId ??= userId; // пока временно — перезапишем ниже реальным creatorId
                        await userStateService.UpdateUserState(userId, userState);

                        // Попробуем получить creatorId и сохранить в state (не критично, можно пропустить)
                        try
                        {
                            var info = await googleSheetsService.GetApplicationInfoAsync(parsedRowId);
                            creatorId = info.creatorId;
                            currentStatus = info.status;
                            userState.OriginalCreatorId = creatorId;
                            await userStateService.UpdateUserState(userId, userState);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Не удалось получить creatorId для заявки {RowId} сразу после выбора — продолжим, попробуем позже", parsedRowId);
                        }

                        await botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            $"✅ Заявка #{parsedRowId} выбрана для закрытия.\n\nПрикрепите фото подтверждения выполнения (можно несколько) или нажмите 'Далее':",
                            replyMarkup: keyboard,
                            cancellationToken: cancellationToken);

                        Log.Information("User {UserId} selected row {RowId} to close", userId, parsedRowId);
                        return;
                    }

                case DialogState.WaitingForClosePhoto:
                    {
                        int rowIdWaiting = GetCurrentRowId();
                        if (rowIdWaiting < 2)
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "Ошибка: не найден идентификатор заявки. Пожалуйста, начните операцию заново через /close.",
                                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                            return; // Здесь НЕ очищаем, чтобы не потерять другие состояния
                        }

                        // Если creatorId ещё не установлен в state — получаем и сохраняем (однократно)
                        if (userState.OriginalCreatorId.GetValueOrDefault(0) == 0)
                        {
                            try
                            {
                                var info = await googleSheetsService.GetApplicationInfoAsync(rowIdWaiting);
                                userState.OriginalCreatorId = info.creatorId;
                                await userStateService.UpdateUserState(userId, userState);
                                creatorId = info.creatorId;
                                currentStatus = info.status;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Ошибка при получении информации о заявке {RowId}", rowIdWaiting);
                                await botClient.SendTextMessageAsync(message.Chat.Id, "Ошибка при получении данных заявки. Попробуйте позже.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                                return;
                            }
                        }
                        else
                        {
                            creatorId = userState.OriginalCreatorId.Value;
                        }

                        // Фото
                        if (message.Photo != null && message.Photo.Length > 0)
                        {
                            var largest = message.Photo.OrderBy(p => p.FileSize ?? 0).Last();
                            var fileId = largest.FileId;
                            if (!userState.ClosureMediaFileIds.Contains(fileId))
                            {
                                userState.ClosureMediaFileIds.Add(fileId);
                                await userStateService.UpdateUserState(userId, userState);
                                Log.Information("Добавлено фото {FileId} для заявки {RowId}", fileId, rowIdWaiting);
                            }

                            if (string.IsNullOrEmpty(message.MediaGroupId))
                            {
                                await botClient.SendTextMessageAsync(message.Chat.Id,
                                    $"📸 Медиафайл добавлен. Всего: {userState.ClosureMediaFileIds.Count} шт. Прикрепите ещё или нажмите 'Далее' для ввода ФИО.",
                                    replyMarkup: keyboard, cancellationToken: cancellationToken);
                            }
                            return;
                        }
                        // Видео
                        else if (message.Video != null)
                        {
                            var fileId = message.Video.FileId;
                            if (!userState.ClosureMediaFileIds.Contains(fileId))
                            {
                                userState.ClosureMediaFileIds.Add(fileId);
                                await userStateService.UpdateUserState(userId, userState);
                                Log.Information("Добавлено видео {FileId} для заявки {RowId}", fileId, rowIdWaiting);
                            }

                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                $"🎥 Медиафайл добавлен. Всего: {userState.ClosureMediaFileIds.Count} шт. Прикрепите ещё или нажмите 'Далее' для ввода ФИО.",
                                replyMarkup: keyboard, cancellationToken: cancellationToken);
                            return;
                        }
                        // Далее
                        else if (string.Equals(message.Text, "Далее", StringComparison.OrdinalIgnoreCase))
                        {
                            userState.State = DialogState.WaitingForCloseContact;
                            userState.PreviousState = DialogState.WaitingForClosePhoto;
                            await userStateService.UpdateUserState(userId, userState);

                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "Укажите ФИО исполнителя и ФИО диспетчера (через запятую, например: 'Иванов И.И., Петров П.П.'):",
                                replyMarkup: keyboard, cancellationToken: cancellationToken);
                            return;
                        }
                        // Назад (отмена закрытия) — откат статуса
                        else if (string.Equals(message.Text, "Назад", StringComparison.OrdinalIgnoreCase))
                        {
                            // Откат статуса на "Открыто"
                            try
                            {
                                await googleSheetsService.UpdateStatusAsync(rowIdWaiting, "Открыто", userId, botClient);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Ошибка при откате статуса для заявки {RowId}", rowIdWaiting);
                            }

                            if (creatorId != 0 && creatorId != userId)
                            {
                                try
                                {
                                    await botClient.SendTextMessageAsync(creatorId,
                                        $"❌ Закрытие вашей заявки #{rowIdWaiting} было отменено. Статус возвращен на 'Открыто'.",
                                        cancellationToken: cancellationToken);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Ошибка при уведомлении создателя {CreatorId}", creatorId);
                                }
                            }

                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                creatorId == userId
                                    ? $"❌ Закрытие вашей заявки #{rowIdWaiting} было отменено. Статус возвращен на 'Открыто'."
                                    : $"❌ Операция закрытия заявки {rowIdWaiting} отменена. Статус возвращен на 'Открыто'.",
                                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);

                            await userStateService.ClearUserState(userId, rowIdWaiting);
                            userState.State = DialogState.None;
                            return;
                        }

                        // Неподдерживаемое сообщение — игнорируем (ожидали фото/Далее/Назад)
                        return;
                    }

                case DialogState.WaitingForCloseContact:
                    {
                        int rowIdContact = GetCurrentRowId();
                        if (rowIdContact < 2)
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "Ошибка: не найден идентификатор заявки. Пожалуйста, начните операцию заново через /close.",
                                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                            await userStateService.ClearUserState(userId, rowIdContact);
                            return;
                        }

                        var parts = (message.Text ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            userState.ExecutorFullName = parts[0].Trim();
                            userState.DispatcherFullName = parts[1].Trim();
                            await userStateService.UpdateUserState(userId, userState);

                            // Перейти к комментарию (опционально)
                            userState.State = DialogState.WaitingForCloseComment;
                            await userStateService.UpdateUserState(userId, userState);

                            var commentKeyboard = new ReplyKeyboardMarkup(new[]
                            {
                            new[] { new KeyboardButton("Пропустить"), new KeyboardButton("Назад") }
                        })
                            { ResizeKeyboard = true };

                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "📝 Добавьте комментарий по выполненной работе (опционально) или нажмите 'Пропустить':",
                                replyMarkup: commentKeyboard,
                                cancellationToken: cancellationToken);
                            return;
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "Пожалуйста, укажите ФИО исполнителя и ФИО диспетчера через запятую (например: 'Иванов И.И., Петров П.П.'):",
                                replyMarkup: keyboard,
                                cancellationToken: cancellationToken);
                            return;
                        }
                    }

                case DialogState.WaitingForCloseComment:
                    {
                        int rowIdFinal = GetCurrentRowId();
                        if (rowIdFinal < 2)
                        {
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "Ошибка: не найден идентификатор заявки. Пожалуйста, начните операцию заново через /close.",
                                replyMarkup: new ReplyKeyboardRemove(),
                                cancellationToken: cancellationToken);
                            await userStateService.ClearUserState(userId, rowIdFinal);
                            return;
                        }

                        if (string.Equals(message.Text, "Пропустить", StringComparison.OrdinalIgnoreCase))
                        {
                            userState.CloseComment = string.Empty;
                            await userStateService.UpdateUserState(userId, userState);
                            await FinishCloseApplication(host, botClient, userState, userId, rowIdFinal, creatorId, cancellationToken);
                            isSuccessfulClose = true;
                        }
                        else if (string.Equals(message.Text, "Назад", StringComparison.OrdinalIgnoreCase))
                        {
                            userState.State = DialogState.WaitingForCloseContact;
                            await userStateService.UpdateUserState(userId, userState);
                            var keyboard2 = new ReplyKeyboardMarkup(new[] { new[] { new KeyboardButton("Далее"), new KeyboardButton("Назад") } }) { ResizeKeyboard = true };
                            await botClient.SendTextMessageAsync(message.Chat.Id,
                                "Укажите ФИО исполнителя и ФИО диспетчера (через запятую, например: 'Иванов И.И., Петров П.П.'):",
                                replyMarkup: keyboard2,
                                cancellationToken: cancellationToken);
                            return;
                        }
                        else
                        {
                            userState.CloseComment = (message.Text ?? "").Trim();
                            userState.Application ??= new Telegrame_Test.Models.Application();
                            userState.Application.CloseComment = userState.CloseComment;
                            await userStateService.UpdateUserState(userId, userState);

                            await FinishCloseApplication(host, botClient, userState, userId, rowIdFinal, creatorId, cancellationToken);
                            isSuccessfulClose = true;
                        }
                        break;
                    }
            }
        }
        finally
        {
            // Очистка только при успешном закрытии (флаг isSuccessfulClose)
            if (isSuccessfulClose)
            {
                await userStateService.ClearUserState(userId, GetCurrentRowId());
                userState.State = DialogState.None;
            }
        }

    }


    private static async Task FinishCloseApplication(IHost host, ITelegramBotClient botClient, UserState userState, long userId, int rowId, long creatorId, CancellationToken cancellationToken)
    {
        var googleSheetsService = host.Services.GetRequiredService<GoogleSheetsService>();
        var directionalSheetsService = host.Services.GetRequiredService<DirectionalSheetsService>();
        var userDatabaseService = host.Services.GetRequiredService<UserDatabaseService>();
        var userStateService = host.Services.GetRequiredService<UserStateService>();

        // ✅ ОТЛАДКА: Проверяем состояние userState
        Log.Information(
            "[DEBUG] FinishCloseApplication: rowId={RowId}, userState.Application != null: {HasApp}, Direction: '{Direction}'",
            rowId,
            userState.Application != null,
            userState.Application?.Direction ?? "NULL");

        try
        {
            await googleSheetsService.UpdateStatusAsync(rowId, "Закрыто", userId, botClient);
            Log.Information("Статус обновлён в основной таблице для заявки {RowId}", rowId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при обновлении статуса заявки {RowId}", rowId);
        }

        // ✅ ОБНОВЛЯЕМ СТАТУС В ОТДЕЛЬНОЙ ТАБЛИЦЕ (если она есть для этого направления)
        if (userState.Application != null && !string.IsNullOrEmpty(userState.Application.Direction))
        {
            Log.Information(
                "[DEBUG] Проверяем отдельную таблицу для направления: '{Direction}'",
                userState.Application.Direction);

            try
            {
                if (directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                {
                    Log.Information(
                        "Направление '{Direction}' имеет отдельную таблицу",
                        userState.Application.Direction);

                    // ✅ ПОЛУЧАЕМ ПРАВИЛЬНЫЙ ROW ID ИЗ БД
                    var mapping = userDatabaseService.GetApplicationMapping(rowId, userState.Application.Direction);

                    Log.Information(
                        "[DEBUG] Маппинг для MainRowId={MainRowId}, Direction={Direction}: {Found}",
                        rowId, userState.Application.Direction, mapping != null);

                    if (mapping != null)
                    {
                        Log.Information(
                            "Маппинг найден: MainRowId={MainRowId}, DirectionRowId={DirectionRowId}",
                            mapping.MainRowId, mapping.DirectionRowId);

                        Log.Information(
                            "Обновляем статус в отдельной таблице для направления '{Direction}' (MainRowId={MainRowId}, DirectionRowId={DirectionRowId})",
                            userState.Application.Direction, mapping.MainRowId, mapping.DirectionRowId);

                        await directionalSheetsService.UpdateStatusInDirectionSheetAsync(
                            rowId: mapping.DirectionRowId,  // ✅ ИСПОЛЬЗУЕМ ПРАВИЛЬНЫЙ ROW ID
                            internalDirectionName: userState.Application.Direction,
                            newStatus: "Закрыто",
                            closedByUserId: userId,
                            botClient: botClient);

                        Log.Information(
                            "Статус успешно обновлён в отдельной таблице для направления '{Direction}'",
                            userState.Application.Direction);
                    }
                    else
                    {
                        Log.Warning(
                            "Маппинг не найден для MainRowId={MainRowId}, Direction={Direction}. Попытка обновить по основному ID...",
                            rowId, userState.Application.Direction);

                        // Fallback: пытаемся обновить по основному ID
                        await directionalSheetsService.UpdateStatusInDirectionSheetAsync(
                            rowId: rowId,
                            internalDirectionName: userState.Application.Direction,
                            newStatus: "Закрыто",
                            closedByUserId: userId,
                            botClient: botClient);
                    }
                }
                else
                {
                    Log.Debug(
                        "Направление '{Direction}' не имеет отдельной таблицы",
                        userState.Application.Direction);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex,
                    "Ошибка при обновлении статуса в отдельной таблице для направления '{Direction}'",
                    userState.Application.Direction);
            }
        }
        else
        {
            Log.Warning(
                "userState.Application равен null или Direction пуст. Пропускаем обновление отдельной таблицы.");
        }

        // Загрузка медиа
        var mediaLinks = new List<string>();
        foreach (var fileId in userState.ClosureMediaFileIds ?? Enumerable.Empty<string>())
        {
            try
            {
                var link = await googleSheetsService.UploadMediaToDriveAsync(fileId, botClient);
                mediaLinks.Add(!string.IsNullOrEmpty(link) ? link : fileId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при загрузке медиа {FileId}", fileId);
                mediaLinks.Add(fileId);
            }
        }

        if (mediaLinks.Any())
        {
            try
            {
                await googleSheetsService.AddClosureMediaAsync(rowId, mediaLinks);

                // ✅ ТАКЖЕ ДОБАВЛЯЕМ МЕДИА В ОТДЕЛЬНУЮ ТАБЛИЦУ (если нужно)
                if (userState.Application != null &&
                    !string.IsNullOrEmpty(userState.Application.Direction) &&
                    directionalSheetsService.HasDirectionSheet(userState.Application.Direction))
                {
                    try
                    {
                        // ✅ ПОЛУЧАЕМ ПРАВИЛЬНЫЙ ROW ID ИЗ БД
                        var mapping = userDatabaseService.GetApplicationMapping(rowId, userState.Application.Direction);
                        var directionRowId = mapping?.DirectionRowId ?? rowId;  // Fallback на основной ID

                        Log.Information(
                            "Добавляем медиафайлы в отдельную таблицу (DirectionRowId={DirectionRowId}) для направления '{Direction}'",
                            directionRowId, userState.Application.Direction);

                        var config = directionalSheetsService.GetDirectionConfig(userState.Application.Direction);
                        if (config != null)
                        {
                            var range = $"'{config.SheetName}'!I{directionRowId}";
                            var valueRange = new Google.Apis.Sheets.v4.Data.ValueRange
                            {
                                Values = new List<IList<object>>
                            {
                                new List<object> { string.Join(", ", mediaLinks) }
                            }
                            };

                            var sheetsService = host.Services.GetRequiredService<Google.Apis.Sheets.v4.SheetsService>();
                            var updateRequest = sheetsService.Spreadsheets.Values.Update(valueRange, config.SpreadsheetId, range);
                            updateRequest.ValueInputOption = Google.Apis.Sheets.v4.SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
                            await updateRequest.ExecuteAsync();

                            Log.Information(
                                "Медиафайлы подтверждения добавлены в отдельную таблицу (DirectionRowId={DirectionRowId}) для направления '{Direction}'",
                                directionRowId, userState.Application.Direction);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex,
                            "Ошибка при добавлении медиа в отдельную таблицу для направления '{Direction}'",
                            userState.Application.Direction);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Ошибка при записи ссылок на медиа в Google Sheets для {RowId}", rowId);
            }
        }

        // Формируем текст уведомления
        string notificationText = $"✅ Заявка #{rowId} {(creatorId == userId ? "ваша" : "")} успешно закрыта" +
            (mediaLinks.Any() ? $" с медиафайлами ({mediaLinks.Count} шт.)" : " без медиафай��ов") +
            $"\n\n👨‍🔧 Исполнитель: {userState.ExecutorFullName}" +
            $"\n📞 Диспетчер: {userState.DispatcherFullName}";

        if (!string.IsNullOrWhiteSpace(userState.CloseComment))
            notificationText += $"\n\n💬 Комментарий: {userState.CloseComment}";

        // Уведомление создателю
        if (creatorId != 0 && creatorId != userId)
        {
            try
            {
                await botClient.SendTextMessageAsync(chatId: creatorId, text: notificationText, cancellationToken: cancellationToken);
                foreach (var mediaId in userState.ClosureMediaFileIds ?? Enumerable.Empty<string>())
                {
                    try
                    {
                        var file = await botClient.GetFileAsync(mediaId);
                        if (!string.IsNullOrEmpty(file.FilePath) && file.FilePath.Contains("video", StringComparison.OrdinalIgnoreCase))
                        {
                            await botClient.SendVideoAsync(chatId: creatorId, video: InputFile.FromFileId(mediaId), caption: "Медиа подтверждени��", cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendPhotoAsync(chatId: creatorId, photo: InputFile.FromFileId(mediaId), caption: "Медиа подтверждение", cancellationToken: cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "❌ Ошибка при отправке медиа создателю {CreatorId}", creatorId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Ошибка при отправке уведомления создателю заявки {RowId}", rowId);
            }
        }

        // Сообщение закрывающему
        try
        {
            await botClient.SendTextMessageAsync(chatId: userId, text: notificationText, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);

            foreach (var mediaId in userState.ClosureMediaFileIds ?? Enumerable.Empty<string>())
            {
                try
                {
                    var file = await botClient.GetFileAsync(mediaId);
                    if (!string.IsNullOrEmpty(file.FilePath) && file.FilePath.Contains("video", StringComparison.OrdinalIgnoreCase))
                    {
                        await botClient.SendVideoAsync(chatId: userId, video: InputFile.FromFileId(mediaId), caption: "Медиа подтверждение", cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendPhotoAsync(chatId: userId, photo: InputFile.FromFileId(mediaId), caption: "Медиа подтверждение", cancellationToken: cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "❌ Ошибка при отправке медиа закрывающему {UserId}", userId);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Ошибка при уведомлении закрывающего {UserId} для заявки {RowId}", userId, rowId);
        }

        Log.Information(
            "Заявка {RowId} закрыта пользователем {UserId} (creatorId={CreatorId}), mediaCount={Count}, commentExists={HasComment}",
            rowId, userId, creatorId, mediaLinks.Count, !string.IsNullOrWhiteSpace(userState.CloseComment));

        await userStateService.ClearUserState(userId, rowId);
    }



    private static async Task HandleRedirectAsync(IHost host, ITelegramBotClient botClient, Message message, UserState userState, UserStateService userStateService, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;

        var googleSheetsService = host.Services.GetRequiredService<GoogleSheetsService>();
        var tgMessageService = host.Services.GetRequiredService<TelegramMessageService>();
        var backoffService = host.Services.GetRequiredService<IAdaptiveBackoffService>();

        if (message.Text == "Отмена")
        {
            await userStateService.ClearUserState(userId, 0);

            await TrySendAsync(ct => botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    "Операция отменена.",
                    replyMarkup: new ReplyKeyboardRemove(),
                    cancellationToken: ct),
                userId, userState.State, cancellationToken, backoffService);

            return;
        }

        switch (userState.State)
        {

            case DialogState.WaitingForRedirectRowId:
                if (message.Text == "Отмена")
                {
                    await userStateService.ClearUserState(userId, 0);
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            "Операция отменена.",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    return;
                }

                if (!int.TryParse(message.Text, out int redirectRowId) || redirectRowId <= 0)
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            "❌ Некорректный ID. Введите число (например: 5)",
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    return;
                }

                // Проверяем, существует ли такая заявка
                var applicationToRedirect = await googleSheetsService.GetApplicationByRowIdAsync(redirectRowId);
                if (applicationToRedirect == null)
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            $"❌ Заявка #{redirectRowId} не найдена.",
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    return;
                }

                // Сохраняем текущую заявку в состояние
                userState.RedirectRowId = redirectRowId;
                userState.RedirectApplication = applicationToRedirect;

                // Получаем доступные чаты
                var chatMappings = configuration.GetSection("ChatMappings").Get<List<ChatMapping>>();
                if (chatMappings == null || !chatMappings.Any())
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            "❌ Нет доступных чатов для перенаправления.",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    await userStateService.ClearUserState(userId, 0);
                    return;
                }

                // Получаем уникальные чаты и их названия
                var uniqueChatsForRedirect = new List<ChatInfo>();
                foreach (var chatGroup in chatMappings.GroupBy(cm => cm.ChatId).Select(g => g.First()))
                {
                    try
                    {
                        var chat = await botClient.GetChatAsync(chatGroup.ChatId);
                        uniqueChatsForRedirect.Add(new ChatInfo
                        {
                            ChatId = chatGroup.ChatId,
                            ChatName = chat.Title ?? "Приватный чат",
                            ChatObject = chatGroup.Object,
                            ChatDirection = chatGroup.Direction
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Ошибка получения инфо о чате {ChatId}", chatGroup.ChatId);
                    }
                }

                if (!uniqueChatsForRedirect.Any())
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            "❌ Ошибка при получении списка чатов.",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    await userStateService.ClearUserState(userId, 0);
                    return;
                }

                // Создаем клавиатуру с названиями чатов
                var rows = new List<KeyboardButton[]>();
                foreach (var chatInfo in uniqueChatsForRedirect)
                {
                    rows.Add(new[] { new KeyboardButton($"{chatInfo.ChatName} ({chatInfo.ChatObject})") });
                }
                rows.Add(new[] { new KeyboardButton("Отмена") });

                var chatKeyboard = new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };

                // Сохраняем информацию о чатах в состояние
                userState.AvailableChatsForRedirect = uniqueChatsForRedirect;
                userState.State = DialogState.WaitingForRedirectChat;
                await userStateService.UpdateUserState(userId, userState);

                await TrySendAsync(ct => botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        $"Заявка #{redirectRowId} найдена.\n\n📍 Выберите чат для перенаправления:",
                        replyMarkup: chatKeyboard,
                        cancellationToken: ct),
                    userId, userState.State, cancellationToken, backoffService);
                return;

            case DialogState.WaitingForRedirectChat:
                if (message.Text == "Отмена")
                {
                    await userStateService.ClearUserState(userId, 0);
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            "Операция отменена.",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    return;
                }

                // Находим выбранный чат
                var selectedChat = userState.AvailableChatsForRedirect?.FirstOrDefault(c =>
                    message.Text.Contains(c.ChatName));

                if (selectedChat == null)
                {
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            "❌ Чат не найден. Пожалуйста, выберите из предложенных вариантов.",
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                    return;
                }

                // Перенаправляем заявку
                try
                {
                    var appToForward = userState.RedirectApplication;

                    // Формируем сообщение
                    var forwardMessage = BuildApplicationMessage(appToForward);

                    // Отправляем в новый чат
                    var sentMessage = await botClient.SendTextMessageAsync(
                        chatId: selectedChat.ChatId,
                        text: forwardMessage,
                        parseMode: ParseMode.Html,
                        cancellationToken: cancellationToken);

                    // Обновляем запись о сообщении в БД
                    await tgMessageService.SaveMessageAsync(
                        appToForward.RowId,
                        selectedChat.ChatId,
                        sentMessage.MessageId);

                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            $"✅ Заявка #{userState.RedirectRowId} успешно перенаправлена в чат '{selectedChat.ChatName}'",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);

                    Log.Information("Заявка {RowId} перенаправлена админом {AdminId} в чат {ChatId}",
                        userState.RedirectRowId, userId, selectedChat.ChatId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Ошибка при перенаправлении заявки {RowId}", userState.RedirectRowId);
                    await TrySendAsync(ct => botClient.SendTextMessageAsync(
                            message.Chat.Id,
                            $"❌ Ошибка при перенаправлении: {ex.Message}",
                            replyMarkup: new ReplyKeyboardRemove(),
                            cancellationToken: ct),
                        userId, userState.State, cancellationToken, backoffService);
                }
                finally
                {
                    await userStateService.ClearUserState(userId, userState.RedirectRowId);
                }
                break;
        }

    }

    private static string BuildApplicationMessage(Application app)
    {
        return $"<b>Измененная заявка #{app.RowId}</b>\n" +
               $"🏢 Объект: {app.Object}\n" +
               $"📍 Местоположение: {app.Location}\n" +
               $"🔧 Направление: {app.Direction}\n" +
               $"⚡ Приоритет: {app.Urgency}\n" +
               (string.IsNullOrEmpty(app.Deadline) ? "" : $"📅 Желаемая дата и время: ({app.Deadline})\n") +
               $"📝 Описание: {app.Description}\n" +
               $"👤 ФИО и контакт: {app.ContactName} {app.ContactPhone}";
    }

    private static string ViewApplicationMessage(Application app)
    {
        return $"🏢 Объект: {app.Object}\n" +
               $"📍 Местоположение: {app.Location}\n" +
               $"🔧 Направление: {app.Direction}\n" +
               $"⚡ Приоритет: {app.Urgency}\n" +
               (string.IsNullOrEmpty(app.Deadline) ? "" : $"📅 Желаемая дата и время: ({app.Deadline})\n") +
               $"📝 Описание: {app.Description}\n" +
               $"👤 ФИО и контакт: {app.ContactName} {app.ContactPhone}";
    }

    static DialogState GetPreviousState(DialogState currentState)
    {
        return currentState switch
        {
            DialogState.WaitingForDirectionCategory => DialogState.WaitingForObject,
            DialogState.WaitingForDirection => DialogState.WaitingForDirectionCategory,
            DialogState.WaitingForLocation => DialogState.WaitingForDirection,
            DialogState.WaitingForUrgency => DialogState.WaitingForLocation,
            DialogState.WaitingForDeadline => DialogState.WaitingForUrgency, 
            DialogState.WaitingForTime => DialogState.WaitingForDeadline,    
            DialogState.WaitingForDescription => DialogState.WaitingForUrgency,
            DialogState.WaitingForPhoto => DialogState.WaitingForDescription,
            DialogState.WaitingForContact => DialogState.WaitingForPhoto,
            _ => DialogState.None
        };
    }

    // ===== МЕТОДЫ ДЛЯ РАБОТЫ С КАТЕГОРИЯМИ НАПРАВЛЕНИЙ (JsonElement версия) =====

    /// <summary>
    /// Получает названия категорий, которые имеют маршруты для выбранного объекта
    /// </summary>
    private static List<string> GetAvailableCategoryNamesForObject(
        IConfiguration configuration,
        List<ChatMapping> chatMappings,
        string selectedObject)
    {
        try
        {
            var availableCategories = new List<string>();
            var categoriesSection = configuration.GetSection("DirectionCategories");

            Serilog.Log.Information("Поиск категорий для объекта: {Object}", selectedObject);

            var categoryChildren = categoriesSection.GetChildren().ToList();
            Serilog.Log.Information("Всего категорий: {Count}", categoryChildren.Count);

            foreach (var categorySection in categoryChildren)
            {
                // Получаем Name
                var nameValue = categorySection.GetSection("Name").Value;
                if (string.IsNullOrEmpty(nameValue))
                {
                    Serilog.Log.Warning("Категория без Name");
                    continue;
                }

                Serilog.Log.Information(" Проверяю категорию: {CategoryName}", nameValue);

                // Получаем Directions как массив
                var directionsSection = categorySection.GetSection("Directions");
                var directions = directionsSection.GetChildren().ToList();

                if (directions.Count == 0)
                {
                    Serilog.Log.Warning("Направлений не найдено");
                    continue;
                }

                Serilog.Log.Information("Направлений в категории: {Count}", directions.Count);

                bool hasMappingInThisCategory = false;

                foreach (var directionSection in directions)
                {
                    var internalName = directionSection.GetSection("InternalName").Value;
                    if (string.IsNullOrEmpty(internalName))
                        continue;

                    var hasMapping = chatMappings?.Any(cm =>
                        cm.Object == selectedObject &&
                        cm.Direction == internalName) ?? false;

                    Serilog.Log.Information("Проверяю маршрут: '{Object}' + '{Direction}' = {HasMapping}",
                        selectedObject, internalName, hasMapping);

                    if (hasMapping)
                    {
                        hasMappingInThisCategory = true;
                        Serilog.Log.Information("НАЙДЕН маршрут!");
                        break;
                    }
                }

                if (hasMappingInThisCategory)
                {
                    Serilog.Log.Information("Категория {CategoryName} ДОБАВЛЕНА", nameValue);
                    availableCategories.Add(nameValue);
                }
                else
                {
                    Serilog.Log.Information("Категория {CategoryName} ПРОПУЩЕНА", nameValue);
                }
            }

            Serilog.Log.Information("ИТОГО найдено категорий: {Count}", availableCategories.Count);
            return availableCategories;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "ОШИБКА в GetAvailableCategoryNamesForObject");
            return new List<string>();
        }
    }

    /// <summary>
    /// Получает направления (DisplayName) для категории, которые существуют в маршрутизации
    /// </summary>
    private static List<string> GetAvailableDirectionsByCategory(
        IConfiguration configuration,
        List<ChatMapping> chatMappings,
        string selectedObject,
        string categoryName)
    {
        try
        {
            var availableDirections = new List<string>();
            var categoriesSection = configuration.GetSection("DirectionCategories");

            Serilog.Log.Information("Получение направлений категории '{CategoryName}' для объекта '{Object}'",
                categoryName, selectedObject);

            var categoryChildren = categoriesSection.GetChildren().ToList();

            foreach (var categorySection in categoryChildren)
            {
                var nameValue = categorySection.GetSection("Name").Value;
                if (nameValue != categoryName)
                    continue;

                var directionsSection = categorySection.GetSection("Directions");
                var directions = directionsSection.GetChildren().ToList();

                Serilog.Log.Information("Найдено {Count} направлений", directions.Count);

                foreach (var directionSection in directions)
                {
                    var displayName = directionSection.GetSection("DisplayName").Value;
                    var internalName = directionSection.GetSection("InternalName").Value;

                    if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(internalName))
                        continue;

                    var hasMapping = chatMappings?.Any(cm =>
                        cm.Object == selectedObject &&
                        cm.Direction == internalName) ?? false;

                    Serilog.Log.Information(" {DisplayName} → {InternalName} = {HasMapping}",
                        displayName, internalName, hasMapping);

                    if (hasMapping)
                    {
                        availableDirections.Add(displayName);
                        Serilog.Log.Information("ДОБАВЛЕНО");
                    }
                }

                break;
            }

            Serilog.Log.Information("Итого направлений: {Count}", availableDirections.Count);
            return availableDirections;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "ОШИБКА в GetAvailableDirectionsByCategory");
            return new List<string>();
        }
    }

    /// <summary>
    /// Получает internal name направления по display name и категории
    /// </summary>
    private static string GetDirectionInternalName(
        IConfiguration configuration,
        string categoryName,
        string displayName)
    {
        try
        {
            var categoriesSection = configuration.GetSection("DirectionCategories");
            var categoryChildren = categoriesSection.GetChildren().ToList();

            foreach (var categorySection in categoryChildren)
            {
                var nameValue = categorySection.GetSection("Name").Value;
                if (nameValue != categoryName)
                    continue;

                var directionsSection = categorySection.GetSection("Directions");
                var directions = directionsSection.GetChildren().ToList();

                foreach (var directionSection in directions)
                {
                    var dispName = directionSection.GetSection("DisplayName").Value;
                    var internalName = directionSection.GetSection("InternalName").Value;

                    if (dispName == displayName && !string.IsNullOrEmpty(internalName))
                    {
                        return internalName;
                    }
                }

                break;
            }

            return displayName;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "ОШИБКА в GetDirectionInternalName");
            return displayName;
        }
    }


    private static async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
    {
        switch (exception)
        {
            // === ШТАТНЫЕ СЛУЧАИ ===

            case Telegram.Bot.Exceptions.RequestException reqEx
                when reqEx.InnerException is TaskCanceledException:
                Log.Debug("Polling timeout (long-poll reconnect)");
                return;

            case OperationCanceledException:
                Log.Debug("Polling cancelled (application stopping)");
                return;

            // === СЕТЬ / TELEGRAM ===

            case ApiRequestException apiEx when apiEx.ErrorCode == 429:
                var delay = apiEx.Parameters?.RetryAfter ?? 30;
                Log.Warning("Rate limit exceeded. Waiting {Delay} sec", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                return;

            case ApiRequestException apiEx when apiEx.ErrorCode == 409:
                Log.Fatal("409 Conflict: another bot instance is running. Shutting down.");
                Environment.Exit(1);
                return;

            case IOException:
            case SocketException:
            case HttpRequestException:
                Log.Warning(exception, "Network error during polling. Waiting before retry.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return;

            // === ВСЁ ОСТАЛЬНОЕ ===

            default:
                Log.Error(exception, "Unhandled polling error. Restart recommended.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return;
        }
    }



    static ReplyKeyboardMarkup BuildCalendarKeyboard(DateTime month)
    {
        var culture = new System.Globalization.CultureInfo("ru-RU");
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var rows = new List<IEnumerable<KeyboardButton>>();

        // Навигация
        rows.Add(new[]
        {
            new KeyboardButton("<"),
            new KeyboardButton(month.ToString("MMMM yyyy", culture)),
            new KeyboardButton(">"),
            new KeyboardButton("Сегодня"),
            new KeyboardButton("Отмена")
        });

        // Дни
        var dayButtons = new List<KeyboardButton>();
        for (int day = 1; day <= daysInMonth; day++)
            dayButtons.Add(new KeyboardButton(day.ToString()));

        for (int i = 0; i < dayButtons.Count; i += 7)
            rows.Add(dayButtons.Skip(i).Take(7));

        return new ReplyKeyboardMarkup(rows)
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true
        };
    }
}

