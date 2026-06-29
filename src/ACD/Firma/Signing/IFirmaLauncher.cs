namespace ACD.Firma.Signing;

public interface IFirmaLauncher
{
    FirmaLaunchResult Launch(FirmaRequest request);
}
