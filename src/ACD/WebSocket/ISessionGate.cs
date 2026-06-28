namespace ACWF.WebSocket;

public interface ISessionGate
{
    bool IsActive { get; }
    Task<bool> TryAcquireAsync(CancellationToken ct);
    void Release();
}