namespace ACD.Firma.Signing;

// Tipos de firma de FirmaONPE (primer token del contrato CLI).
public static class FirmaTipo
{
    public const string FirmaBasico = "FIRMA_BASICO";
    public const string NumerarFirmar = "FIRMA_NUM";
    public const string FirmaAvanzada = "FIRMA_AVA";
    public const string VistoBueno = "VB_FIRMA";
    public const string VistoAvanzado = "VB_AVA";
    public const string FirmaRecepcion = "FIRMA_REC";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>
    {
        FirmaBasico, NumerarFirmar, FirmaAvanzada, VistoBueno, VistoAvanzado, FirmaRecepcion
    };

    public static bool IsSupported(string? tipo) => tipo is not null && Supported.Contains(tipo);
}
