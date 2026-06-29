namespace ACD.Configuration;

public sealed class FirmaOptions
{
    // Override del ejecutable; si se fija, ignora el registro.
    public string? SignerExecutablePath { get; init; }

    public string Area { get; init; } = "";
    public string Telefono { get; init; } = "";
    public string Anexo { get; init; } = "";
    public string Url { get; init; } = "";
}
