using Microsoft.Extensions.Logging;

namespace ACWF.System;

/// <summary>
/// Administra un lock file que anuncia el puerto activo de Kestrel para que otros procesos
/// (ej. una segunda instancia activada vía URI scheme) puedan descubrir el agente corriendo.
/// El archivo es advisory — los fallos se loguean como warnings, no errores.
/// </summary>
public static class PortRegistry
{
    private static readonly ILogger? _logger =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(PortRegistry));

    private static string GetLockFilePath(string packId) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            packId,
            "port.lock");

    /// <summary>
    /// Escribe el número de puerto en el lock file.
    /// Crea el directorio si no existe.
    /// </summary>
    public static void Write(string packId, int port)
    {
        string lockFile = GetLockFilePath(packId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            File.WriteAllText(lockFile, port.ToString());
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Failed to write port lock file at {LockFile}", lockFile);
        }
    }

    /// <summary>
    /// Elimina el lock file en apagado graceful.
    /// </summary>
    public static void Delete(string packId)
    {
        string lockFile = GetLockFilePath(packId);
        try
        {
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
        catch (IOException)
        {
            // Archivo advisory — tragar errores de eliminación.
        }
    }

    /// <summary>
    /// Lee y parsea el puerto desde el lock file. Retorna null en cualquier fallo.
    /// </summary>
    public static int? TryRead(string packId)
    {
        string lockFile = GetLockFilePath(packId);
        try
        {
            if (!File.Exists(lockFile)) return null;
            string content = File.ReadAllText(lockFile);
            return int.TryParse(content.Trim(), out int port) ? port : null;
        }
        catch
        {
            return null;
        }
    }
}
