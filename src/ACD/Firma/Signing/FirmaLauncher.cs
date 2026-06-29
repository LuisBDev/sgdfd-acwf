namespace ACD.Firma.Signing;

public sealed class FirmaLauncher : IFirmaLauncher
{
    private readonly IFirmaCommandBuilder _commandBuilder;
    private readonly ILogger<FirmaLauncher> _logger;
    private readonly IProcessRunner _processRunner;
    private readonly IFirmaSignerResolver _resolver;

    public FirmaLauncher(
        IFirmaSignerResolver resolver,
        IFirmaCommandBuilder commandBuilder,
        IProcessRunner processRunner,
        ILogger<FirmaLauncher> logger)
    {
        _resolver = resolver;
        _commandBuilder = commandBuilder;
        _processRunner = processRunner;
        _logger = logger;
    }

    public FirmaLaunchResult Launch(FirmaRequest request)
    {
        var exe = _resolver.Resolve();
        if (exe is null)
            return FirmaLaunchResult.Failed("SIGNER_NOT_FOUND");

        var arguments = _commandBuilder.Build(request);

        try
        {
            _processRunner.Start(exe, arguments);
            _logger.LogInformation("FirmaONPE lanzado: {Exe} (tipo {Tipo})", exe, request.Tipo);
            return FirmaLaunchResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al lanzar FirmaONPE {Exe}", exe);
            return FirmaLaunchResult.Failed("PROCESS_START_FAILED");
        }
    }
}
