using ACWF.Configuration;
using ACWF.System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ACWF.Firma;

/// <summary>
/// Escribe los bytes del PDF recibido en el watch directory configurado (default C:\TFIRMA).
/// Escritura en streaming — no bufferiza el archivo completo en memoria.
/// </summary>
public sealed class FileDepositService : IFileDepositService
{
    private readonly AcwfOptions _options;
    private readonly ITrayStateNotifier _trayNotifier;
    private readonly ILogger<FileDepositService> _logger;
    private bool _dirUnavailable;

    private const int WriteBufferSize = 64 * 1024; // 64 KB

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

    private void EnsureWatchDirectory()
    {
        try
        {
            Directory.CreateDirectory(_options.WatchDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogCritical(ex,
                "Cannot create or access watch directory {WatchDirectory}. PDF deposits will fail.",
                _options.WatchDirectory);
            _trayNotifier.SetState(TrayState.Error);
            _dirUnavailable = true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Unexpected error initializing watch directory {WatchDirectory}.",
                _options.WatchDirectory);
            _trayNotifier.SetState(TrayState.Error);
            _dirUnavailable = true;
        }
    }

    public async Task<string> DepositAsync(string filename, Stream content, CancellationToken ct)
    {
        // Validar filename — rechazar path traversal y rutas absolutas.
        if (string.IsNullOrWhiteSpace(filename)
            || filename.Contains("..")
            || filename.Contains(Path.DirectorySeparatorChar)
            || filename.Contains(Path.AltDirectorySeparatorChar)
            || Path.IsPathRooted(filename))
        {
            throw new ArgumentException($"Invalid filename: {filename}", nameof(filename));
        }

        if (_dirUnavailable)
        {
            throw new InvalidOperationException("WRITE_FAILED");
        }

        string destPath = Path.Combine(_options.WatchDirectory, filename);

        // Guard de path traversal: confirmar que la ruta resuelta está dentro de WatchDirectory.
        string watchDirNormalized = Path.GetFullPath(_options.WatchDirectory);
        string destNormalized = Path.GetFullPath(destPath);
        if (!destNormalized.StartsWith(watchDirNormalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Filename resolves outside watch directory: {filename}", nameof(filename));
        }

        try
        {
            long bytesWritten = 0;
            await using var fs = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: WriteBufferSize,
                useAsync: true);

            var buffer = new byte[WriteBufferSize];
            int bytesRead;
            while ((bytesRead = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                bytesWritten += bytesRead;
            }

            _logger.LogInformation(
                "PDF written: {Filename}, {Bytes} bytes, path: {DestPath}",
                filename, bytesWritten, destPath);

            return destPath;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Failed to write PDF {Filename} to {WatchDirectory}", filename, _options.WatchDirectory);
            throw new InvalidOperationException("WRITE_FAILED", ex);
        }
    }
}
