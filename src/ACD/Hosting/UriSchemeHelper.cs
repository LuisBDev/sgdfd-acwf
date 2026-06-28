using Microsoft.Win32;

namespace ACWF.Hosting;

/// <summary>
///     Administra las entradas del registro de Windows para el URI scheme de ACWF (acwf:// y acwf-dev://).
///     Las operaciones son idempotentes — seguro llamarlas en cada launch y update.
/// </summary>
public static class UriSchemeHelper
{
    /// <summary>
    ///     Registra el URI scheme bajo HKCU\Software\Classes para que el navegador pueda invocar al agente.
    ///     Crea o sobrescribe el registro existente.
    /// </summary>
    public static void EnsureRegistered(string scheme, string exePath)
    {
        using var classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true)
                                ?? Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using var schemeKey = classesRoot.CreateSubKey(scheme);
        schemeKey.SetValue(null, $"URL:{scheme} Protocol");
        schemeKey.SetValue("URL Protocol", string.Empty);

        using var shellKey = schemeKey.CreateSubKey("shell");
        using var openKey = shellKey.CreateSubKey("open");
        using var commandKey = openKey.CreateSubKey("command");
        commandKey.SetValue(null, $"\"{exePath}\" --uri-invoke \"%1\"");
    }

    /// <summary>
    ///     Elimina el registro del URI scheme del registro de Windows.
    ///     Se llama en el evento de uninstall de Velopack.
    /// </summary>
    public static void Unregister(string scheme)
    {
        using var classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true);
        classesRoot?.DeleteSubKeyTree(scheme, false);
    }
}