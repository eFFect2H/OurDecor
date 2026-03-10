using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;

namespace Telegrame_Test.Services
{
    public interface ITelegramPollingClient
    {
        ITelegramBotClient Client { get; }
    }

    public interface ITelegramSendClient
    {
        ITelegramBotClient Client { get; }
    }

    public sealed class TelegramPollingClient : ITelegramPollingClient
    {
        public ITelegramBotClient Client { get; }

        public TelegramPollingClient(IHttpClientFactory factory, IConfiguration cfg)
        {
            Client = new TelegramBotClient(
                cfg["Telegram:BotToken"],
                factory.CreateClient("TelegramPolling"));
        }
    }

    public sealed class TelegramSendClient : ITelegramSendClient
    {
        public ITelegramBotClient Client { get; }

        public TelegramSendClient(IHttpClientFactory factory, IConfiguration cfg)
        {
            Client = new TelegramBotClient(
                cfg["Telegram:BotToken"],
                factory.CreateClient("TelegramSend"));
        }
    }


}
