using Microsoft.EntityFrameworkCore;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Models;

namespace Telegrame_Test
{
    public class ApplicationDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }
        public DbSet<Invitation> Invitations { get; set; }
        public DbSet<TelegramMessage> TelegramMessages { get; set; }
        public DbSet<ApplicationMapping> ApplicationMappings { get; set; }
        public DbSet<ReminderNotificationLog> ReminderNotificationLogs { get; set; }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                // Получаем директорию, где находится EXE или DLL
                string baseDirectory = AppContext.BaseDirectory;

                // Создаем полный путь к файлу БД
                string dbPath = Path.Combine(baseDirectory, "users.db");

                // ПОДРОБНОЕ ЛОГИРОВАНИЕ
                Log.Information("========== DB CONFIGURATION ==========");
                Log.Information("AppContext.BaseDirectory: {BaseDirectory}", baseDirectory);
                Log.Information("Full DB Path: {DbPath}", dbPath);
                Log.Information("DB File Exists: {Exists}", File.Exists(dbPath));
                Log.Information("Directory Exists: {DirExists}", Directory.Exists(baseDirectory));

                // Убеждаемся, что директория существует
                try
                {
                    Directory.CreateDirectory(baseDirectory);
                    Log.Information("Directory created/verified successfully");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error creating directory: {Message}", ex.Message);
                }

                Log.Information("Connection String: {ConnStr}", $"Data Source={dbPath}");
                Log.Information("========================================");

                optionsBuilder.UseSqlite($"Data Source={dbPath}");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Конфигурация User
            modelBuilder.Entity<User>().HasKey(u => u.TelegramId);
            modelBuilder.Entity<User>().Property(u => u.IsAdmin).HasDefaultValue(false);

            // Конфигурация Invitation
            modelBuilder.Entity<Invitation>().HasKey(i => i.Id);
            modelBuilder.Entity<Invitation>().HasIndex(i => i.Token).IsUnique();
            modelBuilder.Entity<Invitation>().Property(i => i.IsUsed).HasDefaultValue(false);

            // Связь User -> Invitation (опциональная)
            modelBuilder.Entity<User>()
                .HasOne(u => u.Invitation)
                .WithMany()
                .HasForeignKey(u => u.InvitationId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<TelegramMessage>(entity =>
            {
                entity.HasIndex(e => new { e.SheetName, e.RowId }).IsUnique();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            });

            modelBuilder.Entity<ApplicationMapping>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.MainRowId, e.Direction }).IsUnique();  // Уникальность по Main Row и Direction
                entity.HasIndex(e => e.DirectionRowId);
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            });
        }
    }
}
