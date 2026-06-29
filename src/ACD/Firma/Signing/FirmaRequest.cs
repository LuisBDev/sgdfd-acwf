namespace ACD.Firma.Signing;

public sealed record FirmaInstitutionalData(string Area, string Telefono, string Anexo, string Url)
{
    public static readonly FirmaInstitutionalData Empty = new("", "", "", "");
}

public sealed record FirmaRequest(string PdfPath, string Tipo, FirmaInstitutionalData Data);

public sealed record FirmaLaunchResult(bool Success, string? ErrorMessage)
{
    public static FirmaLaunchResult Ok() => new(true, null);
    public static FirmaLaunchResult Failed(string reason) => new(false, reason);
}
