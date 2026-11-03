using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Assistant.Core;

namespace Assistant.App;

/// <summary>
/// Detects when the app was started by a packaged (MSIX) app, such as the Claude desktop app, whose
/// child processes have their AppData writes silently redirected to a private per-package copy. Data
/// written there is invisible when you start the app normally, so settings and connections seem lost.
/// </summary>
public static class LaunchContext
{
    /// <summary>
    /// True when writes to the app's data folder land in some package's private copy instead of the
    /// real folder. Checked by writing a marker file and looking for it under each package's LocalCache.
    /// </summary>
    public static bool AppDataIsRedirected()
    {
        var marker = $".location-probe-{Guid.NewGuid():N}";
        var markerPath = Path.Combine(AppPaths.DataDirectory, marker);
        try
        {
            File.WriteAllText(markerPath, "");

            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages))
            {
                return false;
            }

            var folderName = Path.GetFileName(AppPaths.DataDirectory);
            return Directory.EnumerateDirectories(packages)
                .Any(p => File.Exists(Path.Combine(p, "LocalCache", "Local", folderName, marker)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(markerPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Starts this exe again through Explorer, which launches it outside any package so it uses the real
    /// AppData folder. Command-line arguments don't survive the hop.
    /// </summary>
    public static bool TryRelaunchOutsidePackage()
    {
        if (Environment.ProcessPath is not { } exe || ParentIsExplorer())
        {
            // Already started by Explorer and still redirected: relaunching again would loop.
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool ParentIsExplorer()
    {
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0)
            {
                return false;
            }

            using var parent = Process.GetProcessById((int)info.InheritedFromUniqueProcessId);
            return string.Equals(parent.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returnLength);
}
