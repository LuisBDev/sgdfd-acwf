using ACWF.Configuration;
using ACWF.Tray;
using Microsoft.Extensions.Options;

namespace ACWF.Firma;

/// <summary>
///     Escribe los bytes del PDF recibido en el watch directory configurado (default C:\TFIRMA).
///     Escritura en streaming — no bufferiza el archivo completo en memoria.
/// </summary>
public sealed class FileDepositService : IFileDepositService
{
    private const int WriteBufferSize = 64 * 1024; // 64 KB
    private readonly ILogger<FileDepositService> _logger;
    private readonly AcwfOptions _options;
    private readonly ITrayStateNotifier _trayNotifier;
    private bool _dirUnavailable;

    public FileDepositService(
        IOptions<AcwfOptions> options,
        ITrayStateNotifier trayNotifier,
        ILogger<FileDepositService> logger)
    {
        _options = options.Value;
        _trayNotifier = trayNotifier;
        _logger = logger;

        EnsureWatchDirectory();
    }

    public async Task<string> DepositAsync(string filename, Stream content, CancellationToken ct)
    {
        // Validar nombre de archivo: rechazar path traversal y rutas absolutas.
        if (string.IsNullOrWhiteSpace(filename)
            || filename.Contains("..")
            || filename.Contains(Path.DirectorySeparatorChar)
            || filename.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(filename))
            throw new ArgumentException($"Invalid filename: {filename}", nameof(filename));

        if (_dirUnavailable) throw new InvalidOperationException("WRITE_FAILED");

        var destPath = Path.Combine(_options.WatchDirectory, filename);

        // Verificar que la ruta resuelta esté dentro de WatchDirectory.
        var watchDirNormalized = Path.GetFullPath(_options.WatchDirectory);
        var destNormalized = Path.GetFullPath(destPath);
        if (!destNormalized.StartsWith(watchDirNormalized, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Filename resolves outside watch directory: {filename}", nameof(filename));

        try
        {
            long bytesWritten = 0;
            await using var fs = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                WriteBufferSize,
                true);

            var buffer = new byte[WriteBufferSize];
            int bytesRead;
            while ((bytesRead = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                bytesWritten += bytesRead;
            }

            _logger.LogInformation(
                "PDF escrito: {Filename}, {Bytes} bytes, ruta: {DestPath}",
                filename, bytesWritten, destPath);

            return destPath;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error al escribir el PDF {Filename} en {WatchDirectory}", filename, _options.WatchDirectory);
            throw new InvalidOperationException("WRITE_FAILED", ex);
        }
    }

    public void Cleanup(string originalFilename, string signedSuffix = "[F]")
    {
        var baseName = Path.GetFileNameWithoutExtension(originalFilename);
        var originalPath = Path.Combine(_options.WatchDirectory, originalFilename);
        var signedPath = Path.Combine(_options.WatchDirectory, $"{baseName}{signedSuffix}.pdf");

        TryDelete(originalPath);
        TryDelete(signedPath);
    }

    private void EnsureWatchDirectory()
    {
        try
        {
            Directory.CreateDirectory(_options.WatchDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogCritical(ex,
                "No se puede crear o acceder al directorio de vigilancia {WatchDirectory}. Los depósitos de PDF fallarán.",
                _options.WatchDirectory);
            _trayNotifier.SetState(TrayState.Error);
            _dirUnavailable = true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Error inesperado al inicializar el directorio de vigilancia {WatchDirectory}.",
                _options.WatchDirectory);
            _trayNotifier.SetState(TrayState.Error);
            _dirUnavailable = true;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Archivo eliminado: {Path}", path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo eliminar {Path}", path);
        }
    }
}