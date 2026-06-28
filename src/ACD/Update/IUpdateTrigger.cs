namespace ACWF.Update;

public interface IUpdateTrigger
{
    bool HasPendingUpdate { get; }
    int LastProgress { get; }
    Task CheckNowAsync();
    void ApplyUpdate();
}