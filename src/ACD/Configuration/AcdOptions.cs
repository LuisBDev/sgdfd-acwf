namespace ACD.Configuration;

public sealed class AcdOptions
{
    public int Port { get; init; } = 7272;
    public string WatchDirectory { get; init; } = @"C:\TFIRMA";
    public int FirmaTimeoutSeconds { get; init; } = 300;
    public string[] AllowedOrigins { get; init; } = [];
    public string FirmaSignedSuffix { get; init; } = "[F]";
    public FirmaOptions Firma { get; init; } = new();
}