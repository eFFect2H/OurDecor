using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Telegrame_Test.Services
{
    public class ChatIdDiagnosticService
    {
        private readonly ITelegramBotClient _botClient;

        public ChatIdDiagnosticService(ITelegramBotClient botClient)
        {
            _botClient = botClient;
        }

        /// <summary>
        /// Логирует информацию о чате при получении сообщения
        /// </summary>
        public void LogChatInfo(Message message)
        {
            if (message?.Chat == null)
                return;

            var chatInfo = new
            {
                ChatId = message.Chat.Id,
                ChatType = message.Chat.Type.ToString(),
                ChatTitle = message.Chat.Title ?? message.Chat.FirstName ?? "N/A",
                Username = message.Chat.Username ?? "N/A",
                IsGroupChat = message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup,
                Message = "Информация о чате"
            };

            Log.Information("📊 CHAT INFO: {@ChatInfo}", chatInfo);
            Console.WriteLine($"[CHAT DIAGNOSTICS] ID: {chatInfo.ChatId} | Type: {chatInfo.ChatType} | Title: {chatInfo.ChatTitle}");
        }

        /// <summary>
        /// Проверяет, может ли бот писать в чат
        /// </summary>
        public async Task<bool> CheckBotAccessAsync(long chatId)
        {
            try
            {
                var chat = await _botClient.GetChatAsync(chatId);
                Log.Information("✅ Бот имеет доступ к чату: {ChatId} ({ChatTitle})", chatId, chat.Title ?? "DM");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Бот НЕ имеет доступ к чату: {ChatId}. Ошибка: {Error}", chatId, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Выводит все ChatIds в консоль для копирования в конфиг
        /// </summary>
        public void PrintConfigTemplate(Dictionary<(string Object, string Direction), long> chatMapping)
        {
            Console.WriteLine("\n╔════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║        СКОПИРУЙТЕ ЭТОТ ШАБЛОН В appsettings.json                 ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════════╝\n");

            Console.WriteLine("\"ChatMappings\": [");

            var list = chatMapping.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var key = list[i].Key;
                var chatId = list[i].Value;

                Console.WriteLine($"  {{");
                Console.WriteLine($"    \"Object\": \"{key.Object}\",");
                Console.WriteLine($"    \"Direction\": \"{key.Direction}\",");
                Console.WriteLine($"    \"ChatId\": {chatId}");
                Console.WriteLine($"  }}" + (i < list.Count - 1 ? "," : ""));
            }

            Console.WriteLine("]");
            Console.WriteLine();
        }
    }
}
