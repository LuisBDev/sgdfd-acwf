namespace ACD.Firma.Signing;

public interface IFirmaSignerResolver
{
    // Ruta del ejecutable de FirmaONPE, o null si no está instalado.
    string? Resolve();
}
