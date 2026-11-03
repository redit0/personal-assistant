namespace Assistant.Web.Services;

public enum UpdateState
{
    /// <summary>Running from a build folder rather than an install, so there's nothing to update.</summary>
    NotInstalled,

    /// <summary>No releases repo is set.</summary>
    NotConfigured,
    Idle,
    Checking,
    UpToDate,
    Downloading,

    /// <summary>Downloaded. Applies when you restart.</summary>
    Ready,
    Failed,
}

/// <summary>Checks for and applies app updates. The tray app implements it with Velopack.</summary>
public interface IUpdateService
{
    string CurrentVersion { get; }

    UpdateState State { get; }

    /// <summary>The version waiting to install, when <see cref="State"/> is Ready.</summary>
    string? PendingVersion { get; }

    /// <summary>What went wrong, when <see cref="State"/> is Failed.</summary>
    string? Error { get; }

    DateTime? LastCheckedAt { get; }

    event Action? Changed;

    /// <summary>Checks now and downloads any newer release.</summary>
    Task CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the app, installs the downloaded update, and starts it again.</summary>
    void ApplyAndRestart();
}
