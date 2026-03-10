using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Models;

namespace Telegrame_Test.Services
{
    public class UserDatabaseService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<UserDatabaseService> _logger;
        private readonly IConfiguration _configuration;

        public UserDatabaseService(ApplicationDbContext dbContext, ILogger<UserDatabaseService> logger, IConfiguration configuration)
        {
            _dbContext = dbContext;
            _logger = logger;
            _configuration = configuration;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                _dbContext.Database.EnsureCreated();

                // Инициализация админов из appsettings.json
                var admins = _configuration.GetSection("Admins").Get<List<string>>();
                if (admins != null && admins.Any())
                {
                    foreach (var adminIdStr in admins)
                    {
                        if (long.TryParse(adminIdStr, out long adminId) && !_dbContext.Users.Any(u => u.TelegramId == adminId))
                        {
                            _dbContext.Users.Add(new User
                            {
                                TelegramId = adminId,
                                IsAdmin = true,
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }
                    _dbContext.SaveChanges();
                    _logger.LogInformation("Админы из appsettings.json инициализированы в базе данных");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при инициализации базы данных пользователей");
                throw;
            }
        }

        public async Task<bool> IsUserAdmin(long telegramId)
        {
            try
            {
                return await _dbContext.Users.AnyAsync(u => u.TelegramId == telegramId && u.IsAdmin);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке, является ли пользователь администратором {TelegramId}", telegramId);
                return false;
            }
        }

        /// <summary>
        /// Проверяет, является ли пользователь администратором
        /// </summary>
        public async Task<bool> IsUserAllowed(long telegramId)
        {
            try
            {
                // Пользователь имеет доступ, если он существует в БД
                var userExists = await _dbContext.Users.AnyAsync(u => u.TelegramId == telegramId);

                if (userExists)
                {
                    _logger.LogDebug("Пользователь {TelegramId} найден в БД, доступ предоставлен", telegramId);
                }
                else
                {
                    _logger.LogDebug("Пользователь {TelegramId} не найден в БД, доступ запрещен", telegramId);
                }

                return userExists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке пользователя {TelegramId}", telegramId);
                return false;
            }
        }

        /// <summary>
        /// Добавляет пользователя в БД с привязкой к приглашению
        /// </summary>
        public void AddUser(long telegramId, bool isAdmin, int? invitationId = null)
        {
            try
            {
                if (!_dbContext.Users.Any(u => u.TelegramId == telegramId))
                {
                    _dbContext.Users.Add(new User
                    {
                        TelegramId = telegramId,
                        IsAdmin = isAdmin,
                        CreatedAt = DateTime.UtcNow,
                        InvitationId = invitationId
                    });
                    _dbContext.SaveChanges();
                    _logger.LogInformation(
                        "Пользователь {TelegramId} добавлен с ролью IsAdmin={IsAdmin}, InvitationId={InvitationId}",
                        telegramId, isAdmin, invitationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при добавлении пользователя {TelegramId}", telegramId);
                throw;
            }
        }

        /// <summary>
        /// Проверяет, существует ли пользователь в БД
        /// </summary>
        public bool UserExists(long telegramId)
        {
            try
            {
                return _dbContext.Users.Any(u => u.TelegramId == telegramId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при проверке существования пользователя {TelegramId}", telegramId);
                return false;
            }
        }

        public async Task<ApplicationMapping> SaveApplicationMappingAsync(int mainRowId, int directionRowId, string direction, string directionSpreadsheetId, string directionSheetName)
        {
            try
            {
                // Проверяем, есть ли уже маппинг для этой заявки и направления
                var existingMapping =  _dbContext.ApplicationMappings
                    .FirstOrDefault(m => m.MainRowId == mainRowId && m.Direction == direction);

                if (existingMapping != null)
                {
                    // Обновляем существующий маппинг
                    existingMapping.DirectionRowId = directionRowId;
                    existingMapping.DirectionSpreadsheetId = directionSpreadsheetId;
                    existingMapping.DirectionSheetName = directionSheetName;
                    existingMapping.UpdatedAt = DateTime.UtcNow;

                    _dbContext.ApplicationMappings.Update(existingMapping);
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation(
                        "Маппинг заявки обновлён: MainRowId={MainRowId}, DirectionRowId={DirectionRowId}, Direction={Direction}",
                        mainRowId, directionRowId, direction);

                    return existingMapping;
                }

                // Создаём новый маппинг
                var newMapping = new ApplicationMapping
                {
                    MainRowId = mainRowId,
                    DirectionRowId = directionRowId,
                    Direction = direction,
                    DirectionSpreadsheetId = directionSpreadsheetId,
                    DirectionSheetName = directionSheetName,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.ApplicationMappings.Add(newMapping);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation(
                    "Маппинг заявки сохранён: MainRowId={MainRowId}, DirectionRowId={DirectionRowId}, Direction={Direction}",
                    mainRowId, directionRowId, direction);

                return newMapping;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Ошибка при сохранении маппинга заявки: MainRowId={MainRowId}, DirectionRowId={DirectionRowId}",
                    mainRowId, directionRowId);
                throw;
            }
        }

        /// <summary>
        /// Получает маппинг заявки по MainRowId и Direction
        /// </summary>
        public ApplicationMapping GetApplicationMapping(int mainRowId, string direction)
        {
            try
            {
                var mapping = _dbContext.ApplicationMappings
                    .FirstOrDefault(m => m.MainRowId == mainRowId && m.Direction == direction);

                if (mapping != null)
                {
                    _logger.LogDebug(
                        "Маппинг найден: MainRowId={MainRowId}, DirectionRowId={DirectionRowId}, Direction={Direction}",
                        mapping.MainRowId, mapping.DirectionRowId, mapping.Direction);
                }
                else
                {
                    _logger.LogDebug(
                        "Маппинг не найден: MainRowId={MainRowId}, Direction={Direction}",
                        mainRowId, direction);
                }

                return mapping;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Ошибка при получении маппинга: MainRowId={MainRowId}, Direction={Direction}",
                    mainRowId, direction);
                return null;
            }
        }

        /// <summary>
        /// Получает маппинг заявки по MainRowId (может быть несколько для разных направлений)
        /// </summary>
        public List<ApplicationMapping> GetApplicationMappingsByMainRowId(int mainRowId)
        {
            try
            {
                var mappings = _dbContext.ApplicationMappings
                    .Where(m => m.MainRowId == mainRowId)
                    .ToList();

                _logger.LogDebug(
                    "Найдено {Count} маппингов для MainRowId={MainRowId}",
                    mappings.Count, mainRowId);

                return mappings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Ошибка при получении маппингов: MainRowId={MainRowId}",
                    mainRowId);
                return new List<ApplicationMapping>();
            }
        }

        /// <summary>
        /// Удаляет маппинг (например, при удалении заявки)
        /// </summary>
        public async Task<bool> DeleteApplicationMappingAsync(int mainRowId, string direction)
        {
            try
            {
                var mapping = _dbContext.ApplicationMappings
                    .FirstOrDefault(m => m.MainRowId == mainRowId && m.Direction == direction);

                if (mapping != null)
                {
                    _dbContext.ApplicationMappings.Remove(mapping);
                    await _dbContext.SaveChangesAsync();

                    _logger.LogInformation(
                        "Маппинг удалён: MainRowId={MainRowId}, Direction={Direction}",
                        mainRowId, direction);

                    return true;
                }

                _logger.LogDebug(
                    "Маппинг не найден для удаления: MainRowId={MainRowId}, Direction={Direction}",
                    mainRowId, direction);

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Ошибка при удалении маппинга: MainRowId={MainRowId}, Direction={Direction}",
                    mainRowId, direction);
                throw;
            }
        }

        /// <summary>
        /// Проверяет, существует ли маппинг для заявки
        /// </summary>
        public async Task<bool> ApplicationMappingExists(int mainRowId, string direction)
        {
            try
            {
                return await _dbContext.ApplicationMappings.AnyAsync(m => m.MainRowId == mainRowId && m.Direction == direction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Ошибка при проверке существования маппинга: MainRowId={MainRowId}, Direction={Direction}",
                    mainRowId, direction);
                return false;
            }
        }
    }
}
