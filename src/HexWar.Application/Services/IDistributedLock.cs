namespace HexWar.Application.Services;

public interface IDistributedLock
{
    /// <summary>
    /// 락 획득 시도
    /// </summary>
    /// <param name="resourceKey">락 대상 리소스 키 (예: gameroom:room-123)</param>
    /// <param name="expiry">락 만료 시간 (소유자가 크래시해도 자동 해제)</param>
    /// <returns>락 획득 성공 시 IDisposable (Dispose 시 해제)</returns>
    Task<IDistributedLockHandle?> TryAcquireAsync(string resourceKey, TimeSpan expiry);
}

/// <summary>
/// 획득한 락의 핸들. Dispose 시 락이 해제됩니다.
/// </summary>
public interface IDistributedLockHandle : IDisposable
{
    string ResourceKey { get; }
    string LockId { get; }
}