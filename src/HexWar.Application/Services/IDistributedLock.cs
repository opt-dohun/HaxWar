namespace HexWar.Application.Services;

public interface IDistributedLock
{
    /// <summary>
    /// 락 획득 시도. acquireTimeout 내에 재시도하며 대기합니다.
    /// </summary>
    /// <param name="resourceKey">락 대상 리소스 키 (예: gameroom:room-123)</param>
    /// <param name="expiry">락 만료 시간 (소유자가 크래시해도 자동 해제)</param>
    /// <param name="acquireTimeout">락 획득을 포기하기까지 대기할 최대 시간. null이면 단발 시도.</param>
    /// <returns>락 획득 성공 시 IDisposable (Dispose 시 해제), 타임아웃 시 null</returns>
    Task<IDistributedLockHandle?> TryAcquireAsync(string resourceKey, TimeSpan expiry, TimeSpan? acquireTimeout = null);
}

/// <summary>
/// 획득한 락의 핸들. Dispose 시 락이 해제됩니다.
/// </summary>
public interface IDistributedLockHandle : IDisposable
{
    string ResourceKey { get; }
    string LockId { get; }
}