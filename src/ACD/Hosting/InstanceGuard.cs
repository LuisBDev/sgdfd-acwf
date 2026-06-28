namespace ACWF.Hosting;

/// <summary>
///     Aplica comportamiento de single-instance por environment usando un named global Mutex.
///     Si el mutex ya está tomado por otro proceso, sale inmediatamente con código 0.
/// </summary>
public static class InstanceGuard
{
    /// <summary>
    ///     Intenta adquirir el mutex global para la variante de environment dada.
    ///     Llama a <see cref="Environment.Exit(int)" /> silenciosamente si otra instancia ya está corriendo.
    /// </summary>
    /// <param name="environment">Sufijo del environment: "Dev" o "Prod".</param>
    /// <returns>Un <see cref="IDisposable" /> que libera el mutex al hacer dispose.</returns>
    public static IDisposable Acquire(string environment)
    {
        var mutexName = $"Global\\ACWF-{environment}";
        var mutex = new Mutex(true, mutexName, out var isNewInstance);

        if (!isNewInstance)
        {
            mutex.Dispose();
            Environment.Exit(0);
        }

        return new MutexHandle(mutex);
    }

    private sealed class MutexHandle : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _disposed;

        internal MutexHandle(Mutex mutex)
        {
            _mutex = mutex;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                /* ya liberado */
            }

            _mutex.Dispose();
        }
    }
}