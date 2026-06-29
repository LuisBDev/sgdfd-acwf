namespace ACD.Firma.Signing;

public interface IFirmaCommandBuilder
{
    string Build(FirmaRequest request);
}
