using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models.Proxy
{
    /// <summary>
    /// Конфигурация для работы с прокси и сетевыми настройками
    /// </summary>
    public class ProxyConfiguration
    {
        public bool Enabled { get; set; } = false;
        public string? Type { get; set; } // "http", "socks5"
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }

        // Очередь прокси для fallback 
        public List<ProxyConfiguration>? Fallback { get; set; }
    }

    /// <summary>
    /// Расширенная конфигурация resilience
    /// </summary>
    public class AdvancedResilienceConfig
    {
        public int MaxRetryAttempts { get; set; } = 3;
        public int InitialDelayMs { get; set; } = 100;
        public int MaxDelayMs { get; set; } = 5000;
        public double BackoffMultiplier { get; set; } = 2.0;
        public bool UseJitter { get; set; } = true;

        // Circuit breaker
        public double CircuitBreakerFailureRatio { get; set; } = 0.5;
        public int CircuitBreakerMinimumThroughput { get; set; } = 10;
        public int CircuitBreakerBreakDurationSec { get; set; } = 10;

        // Timeout
        public int ConnectTimeoutSec { get; set; } = 10;
        public int AttemptTimeoutSec { get; set; } = 15;
        public int TotalTimeoutSec { get; set; } = 45;

        // Адаптивность
        public bool UseAdaptiveBackoff { get; set; } = true;
        public bool EnableNetworkDiagnostics { get; set; } = true;
    }
}
