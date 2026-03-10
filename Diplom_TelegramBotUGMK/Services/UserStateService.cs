using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Models;

namespace Telegrame_Test.Service
{
    public class UserStateService
    {
        private readonly ConcurrentDictionary<string, UserState> _states = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        private SemaphoreSlim GetLock(string key)
        {
            return _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private static string BuildKey(long userId, int? rowId)
        {
            return rowId.HasValue
                ? $"{userId}::RowId_{rowId.Value}"
                : userId.ToString();
        }

        // -----------------------------
        // GET
        // -----------------------------
        public async Task<UserState> GetUserState(long userId, int? rowId = null)
        {
            var key = BuildKey(userId, rowId);
            var semaphore = GetLock(key);

            await semaphore.WaitAsync();
            try
            {
                Serilog.Log.Debug("GetUserStateAsync key: {Key}", key);

                if (!_states.TryGetValue(key, out var state))
                {
                    state = new UserState
                    {
                        Application = new Application
                        {
                            TelegramUserId = userId
                        }
                    };

                    _states[key] = state;
                }

                state.LastActivity = DateTime.UtcNow;
                return state;
            }
            finally
            {
                semaphore.Release();
            }
        }

        // -----------------------------
        // UPDATE
        // -----------------------------
        public async Task UpdateUserState(long userId, UserState state)
        {
            var key = BuildKey(userId, state.RowId);
            var semaphore = GetLock(key);

            await semaphore.WaitAsync();
            try
            {
                Serilog.Log.Debug("UpdateUserStateAsync key: {Key}", key);

                state.LastActivity = DateTime.UtcNow;
                _states[key] = state;
            }
            finally
            {
                semaphore.Release();
            }
        }

        // -----------------------------
        // CLEAR
        // -----------------------------
        public async Task ClearUserState(long userId, int? rowId = null)
        {
            var key = BuildKey(userId, rowId);
            var semaphore = GetLock(key);

            await semaphore.WaitAsync();
            try
            {
                Serilog.Log.Debug("ClearUserStateAsync key: {Key}", key);

                _states.TryRemove(key, out _);
            }
            finally
            {
                semaphore.Release();
                _locks.TryRemove(key, out _); // освобождаем lock
            }
        }

        // -----------------------------
        // CLEANUP
        // -----------------------------
        public async Task CleanupOldStates(TimeSpan expirationTime)
        {
            var now = DateTime.UtcNow;
            var keysToRemove = new List<string>();

            foreach (var kvp in _states)
            {
                var state = kvp.Value;
                if (state.LastActivity.HasValue &&
                    (now - state.LastActivity.Value) > expirationTime)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                if (_states.TryRemove(key, out _))
                {
                    _locks.TryRemove(key, out _);
                    Serilog.Log.Debug("Очищено старое состояние по ключу: {Key}", key);
                }
            }

            Serilog.Log.Information("Очищено {Count} старых состояний", keysToRemove.Count);
            await Task.CompletedTask;
        }

    }
}
