using System;
using StackExchange.Redis;

namespace HexWar.Infrastructure.Persistence;

/// <summary>
/// Redis 연결 설정
/// </summary>
public class RedisConfiguration
{
    public const string SectionName = "Redis";

    /// <summary>연결 문자열 (host:port)</summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>비밀번호 (선택)</summary>
    public string? Password { get; set; }

    /// <summary>데이터베이스 인덱스 (기본 0)</summary>
    public int Database { get; set; } = 0;

    /// <summary>연결 재시도 횟수</summary>
    public int ConnectRetry { get; set; } = 3;

    /// <summary>연결 타임아웃 (ms)</summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>동기 타임아웃 (ms)</summary>
    public int SyncTimeout { get; set; } = 5000;

    /// <summary>게임 세션 TTL</summary>
    public int GameSessionExpiryMinutes { get; set; } = 60;

    /// <summary>종료된 게임 TTL</summary>
    public int GameOverExpiryMinutes { get; set; } = 5;

    /// <summary>매칭 큐 TTL</summary>
    public int MatchmakingQueueExpiryMinutes { get; set; } = 10;

    /// <summary>풀링된 연결 유지 시간</summary>
    public int PooledConnectionLifetimeMinutes { get; set; } = 30;

    /// <summary>
    /// StackExchange.Redis ConfigurationOptions 생성
    /// </summary>
    public ConfigurationOptions ToConfigurationOptions()
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { ConnectionString },
            Password = Password,
            DefaultDatabase = Database,
            ConnectRetry = ConnectRetry,
            ConnectTimeout = ConnectTimeout,
            SyncTimeout = SyncTimeout,
            AbortOnConnectFail = false,  // Redis 없어도 서버는 시작
            AllowAdmin = false,
            Ssl = false
        };

        return options;
    }
}