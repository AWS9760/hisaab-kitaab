using System.Diagnostics;

namespace HisaabKitaab.Services;

/// <summary>
/// Opens files in the program the OS associates with them (Excel, LibreOffice…).
/// </summary>
public interface ILauncherService
{
    void OpenFile(string path);
}

public class LauncherService : ILauncherService
{
    public void OpenFile(string path)
    {
        // UseShellExecute goes through the Windows shell, or xdg-open on Linux.
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
