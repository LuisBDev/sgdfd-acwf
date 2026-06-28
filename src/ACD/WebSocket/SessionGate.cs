namespace ACWF.WebSocket;

/// <summary>
///     Gate thread-safe singleton que asegura a lo sumo una sesión WebSocket activa.
///     Usa SemaphoreSlim(1,1) para adquisición atómica.
/// </summary>
public sealed class SessionGate : ISessionGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _isActive;

    public bool IsActive => _isActive;

    /// <summary>
    ///     Intenta adquirir el session gate. Retorna false inmediatamente si ya hay una sesión activa.
    /// </summary>
    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        var acquired = await _lock.WaitAsync(0, ct).ConfigureAwait(false);
        if (acquired) _isActive = true;
        return acquired;
    }

    /// <summary>
    ///     Libera el gate, permitiendo que nuevas sesiones se conecten.
    /// </summary>
    public void Release()
    {
        _isActive = false;
        _lock.Release();
    }
}