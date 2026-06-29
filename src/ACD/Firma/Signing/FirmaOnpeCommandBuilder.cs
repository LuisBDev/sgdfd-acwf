namespace ACD.Firma.Signing;

// Formato: "tipo@rutaPdf@area&#telefono&#anexo&#url"
public sealed class FirmaOnpeCommandBuilder : IFirmaCommandBuilder
{
    public string Build(FirmaRequest request)
    {
        var d = request.Data;
        var datos = string.Join("&#", d.Area, d.Telefono, d.Anexo, d.Url);
        return $"\"{request.Tipo}@{request.PdfPath}@{datos}\"";
    }
}
