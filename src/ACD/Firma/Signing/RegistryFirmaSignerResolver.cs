using ACD.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ACD.Firma.Signing;

public sealed class RegistryFirmaSignerResolver : IFirmaSignerResolver
{
    private readonly ILogger<RegistryFirmaSignerResolver> _logger;
    private readonly FirmaOptions _options;

    public RegistryFirmaSignerResolver(IOptions<AcdOptions> options, ILogger<RegistryFirmaSignerResolver> logger)
    {
        _options = options.Value.Firma;
        _logger = logger;
    }

    public string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_options.SignerExecutablePath))
            return _options.SignerExecutablePath.Trim();

        // Vista de 32 bits: FirmaONPE es x86.
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\FirmaONPE", writable: false);
            var exe = (key?.GetValue("exeFirmaOnpe") as string)?.Trim();
            if (!string.IsNullOrWhiteSpace(exe))
                return exe;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al leer la ruta de FirmaONPE del registro");
        }

        _logger.LogError("FirmaONPE no está instalado (HKLM\\SOFTWARE\\FirmaONPE\\exeFirmaOnpe).");
        return null;
    }
}
