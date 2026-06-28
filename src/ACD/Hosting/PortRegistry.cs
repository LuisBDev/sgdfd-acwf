namespace ACWF.Hosting;

/// <summary>
///     Administra un lock file que anuncia el puerto activo de Kestrel para que otros procesos
///     (ej. una segunda instancia activada vía URI scheme) puedan descubrir el agente corriendo.
///     El archivo es advisory — los fallos se loguean como warnings, no errores.
/// </summary>
public static class PortRegistry
{
    private static readonly ILogger? _logger =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(PortRegistry));

    private static string GetLockFilePath(string packId)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            packId,
            "port.lock");
    }

    public static void Write(string packId, int port)
    {
        var lockFile = GetLockFilePath(packId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            File.WriteAllText(lockFile, port.ToString());
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Error al escribir el archivo lock del puerto en {LockFile}", lockFile);
        }
    }

    public static void Delete(string packId)
    {
        var lockFile = GetLockFilePath(packId);
        try
        {
            if (File.Exists(lockFile)) File.Delete(lockFile);
        }
        catch (IOException)
        {
            // Archivo advisory — ignorar errores de eliminación.
        }
    }

    public static int? TryRead(string packId)
    {
        var lockFile = GetLockFilePath(packId);
        try
        {
            if (!File.Exists(lockFile)) return null;
            var content = File.ReadAllText(lockFile);
            return int.TryParse(content.Trim(), out var port) ? port : null;
        }
        catch
        {
            return null;
        }
    }
}