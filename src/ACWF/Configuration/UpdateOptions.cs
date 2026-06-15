namespace ACWF.Configuration;

public sealed class UpdateOptions
{
    public int CheckIntervalHours { get; init; } = 6;
    public string RepoUrl { get; init; } = "";

    /// <summary>
    ///     Canal de Velopack del que esta variante recibe actualizaciones.
    ///     Debe coincidir con el --channel usado al empaquetar en CI
    ///     (prod = "stable", dev = "dev").
    /// </summary>
    public string Channel { get; init; } = "stable";

    /// <summary>
    ///     Si true, considera los GitHub releases marcados como pre-release
    ///     (variante dev). Debe ir alineado con el canal.
    /// </summary>
    public bool IncludePrerelease { get; init; }

    /// <summary>
    ///     Token OAuth opcional para leer releases de un repositorio privado.
    ///     Vacío para repositorios públicos.
    /// </summary>
    public string AccessToken { get; init; } = "";
}
