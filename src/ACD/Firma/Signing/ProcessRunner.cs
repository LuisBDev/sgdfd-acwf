using System.Diagnostics;

namespace ACD.Firma.Signing;

public sealed class ProcessRunner : IProcessRunner
{
    public void Start(string fileName, string arguments)
    {
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false
        });
    }
}
