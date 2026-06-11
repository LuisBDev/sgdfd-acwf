using Microsoft.Win32;

namespace ACWF.System;

/// <summary>
/// Administra las entradas del registro de Windows para el URI scheme de ACWF (acwf:// y acwf-dev://).
/// Las operaciones son idempotentes — seguro llamarlas en cada launch y update.
/// </summary>
public static class UriSchemeHelper
{
    /// <summary>
    /// Registra el URI scheme bajo HKCU\Software\Classes para que el navegador pueda invocar al agente.
    /// Crea o sobrescribe el registro existente.
    /// </summary>
    /// <param name="scheme">Nombre del URI scheme, ej. "acwf" o "acwf-dev".</param>
    /// <param name="exePath">Ruta absoluta al ejecutable a invocar.</param>
    public static void EnsureRegistered(string scheme, string exePath)
    {
        using RegistryKey classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using RegistryKey schemeKey = classesRoot.CreateSubKey(scheme);
        schemeKey.SetValue(null, $"URL:{scheme} Protocol");
        schemeKey.SetValue("URL Protocol", string.Empty);

        using RegistryKey shellKey = schemeKey.CreateSubKey("shell");
        using RegistryKey openKey = shellKey.CreateSubKey("open");
        using RegistryKey commandKey = openKey.CreateSubKey("command");
        commandKey.SetValue(null, $"\"{exePath}\" --uri-invoke \"%1\"");
    }

    /// <summary>
    /// Elimina el registro del URI scheme del registro de Windows.
    /// Se llama en el evento de uninstall de Velopack.
    /// </summary>
    /// <param name="scheme">Nombre del URI scheme a desregistrar.</param>
    public static void Unregister(string scheme)
    {
        using RegistryKey? classesRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
        classesRoot?.DeleteSubKeyTree(scheme, throwOnMissingSubKey: false);
    }
}
