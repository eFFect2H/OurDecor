using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Models;

namespace Telegrame_Test.Services
{
    public class TelegramMessageService
    {
        private readonly ApplicationDbContext _dbContext;

        public TelegramMessageService(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task SaveMessageAsync(int rowId, long chatId, int messageId)
        {
            var exists = await _dbContext.TelegramMessages.AnyAsync(t => t.RowId == rowId);
            if (!exists)
            {
                _dbContext.TelegramMessages.Add(new TelegramMessage
                {
                    SheetName = "Заметки",
                    RowId = rowId,
                    ChatId = chatId,
                    MessageId = messageId
                });
                await _dbContext.SaveChangesAsync();
            }
        }

        public async Task<TelegramMessage?> GetMessageAsync(int rowId)
        {
            return await _dbContext.TelegramMessages.FirstOrDefaultAsync(x => x.RowId == rowId && x.SheetName == "Заметки");
        }
    }
}
