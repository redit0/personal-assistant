using Assistant.Web.Services;
using Microsoft.Win32;

namespace Assistant.App;

/// <summary>
/// Starts the app when you sign in to Windows, through your own Run key, so it needs no admin rights. The
/// installer turns this on and uninstalling turns it off. Turning the app off in Task Manager's Startup apps
/// counts as off here too.
/// </summary>
public sealed class WindowsStartup : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "PersonalAssistant";

    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is null)
            {
                return false;
            }

            // Task Manager leaves the Run entry alone and marks it disabled here. An odd first byte means off.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    /// <summary>Points the Run entry at this exe. In an installed copy that path stays the same across updates.</summary>
    public static void Enable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            run.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }

        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void Disable()
    {
        foreach (var key in new[] { RunKey, ApprovedKey })
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
