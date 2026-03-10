using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Models;

namespace Telegrame_Test.Services
{
    public class InvitationService
    {
        private readonly IConfiguration _configuration;
        private readonly UserDatabaseService _userDbService;
        private readonly ApplicationDbContext _dbContext;

        public InvitationService(IConfiguration configuration, UserDatabaseService userDbService, ApplicationDbContext dbContext)
        {
            _configuration = configuration;
            _userDbService = userDbService;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Генерирует приглашение и сохраняет его в БД
        /// </summary>
        public string GenerateInvitation(long adminId)
        {
            
            var token = Guid.NewGuid().ToString();
            var invitation = new Invitation
            {
                Token = token,
                InvitedBy = adminId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24),
                IsUsed = false,
                UsedAt = null
            };

            _dbContext.Invitations.Add(invitation);
            _dbContext.SaveChanges();

            Log.Information("Сгенерировано приглашение с токеном {Token} для администратора {AdminId}", token, adminId);
            return $"https://t.me/UGMK_Supportbot?start={token}";
        }

        /// <summary>
        /// Проверяет и активирует приглашение
        /// </summary>
        public bool ValidateInvitation(string token, out long invitedBy, long userId)
        {
            Log.Debug("Начало проверки токена {Token} для пользователя {UserId}", token, userId);
            invitedBy = 0;

            try
            {
                // Ищем приглашение в БД
                var invitation = _dbContext.Invitations.FirstOrDefault(i => i.Token == token);

                if (invitation == null)
                {
                    Log.Warning("Токен {Token} не найден для пользователя {UserId}", token, userId);
                    return false;
                }

                // Проверяем, что токен еще не использован и не истек
                if (invitation.IsUsed)
                {
                    Log.Warning("Токен {Token} уже использован для пользователя {UserId}", token, userId);
                    return false;
                }

                if (invitation.ExpiresAt <= DateTime.UtcNow)
                {
                    Log.Warning("Токен {Token} истек для пользователя {UserId}", token, userId);
                    return false;
                }

                // Активируем приглашение
                invitation.IsUsed = true;
                invitation.UsedAt = DateTime.UtcNow;
                invitation.InvitedUserId = userId;

                // Добавляем пользователя в БД
                _userDbService.AddUser(userId, false, invitation.Id);

                _dbContext.SaveChanges();

                invitedBy = invitation.InvitedBy;
                Log.Information("Пользователь {UserId} добавлен в базу данных через приглашение {InvitationId}",
                    userId, invitation.Id);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при валидации токена {Token} для пользователя {UserId}", token, userId);
                return false;
            }
        }

        /// <summary>
        /// Очищает истекшие приглашения из БД
        /// </summary>
        public void CleanupExpiredInvitations()
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredCount = _dbContext.Invitations
                    .Where(i => i.ExpiresAt < now && !i.IsUsed)
                    .ExecuteDelete();

                Log.Information("Удалено {Count} истекших приглашений", expiredCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при очистке истекших приглашений");
            }
        }

        /// <summary>
        /// Получает информацию о приглашении (для администраторов)
        /// </summary>
        public Invitation GetInvitationByToken(string token)
        {
            return _dbContext.Invitations.FirstOrDefault(i => i.Token == token);
        }

        /// <summary>
        /// Получает все приглашения, созданные администратором
        /// </summary>
        public List<Invitation> GetInvitationsByAdmin(long adminId)
        {
            return _dbContext.Invitations
                .Where(i => i.InvitedBy == adminId)
                .OrderByDescending(i => i.CreatedAt)
                .ToList();
        }
    }
}
