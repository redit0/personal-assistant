namespace Assistant.Core.Repos;

/// <summary>Maps a remote repository to the local clone where an agent should work on it.</summary>
public class RepoMapping
{
    public int Id { get; set; }

    /// <summary>Repo name, usually the last segment of the remote URL.</summary>
    public required string Name { get; set; }

    public required string RemoteUrl { get; set; }

    public required string LocalPath { get; set; }

    public CodeHost Host { get; set; }

    public DateTime LastSeenAt { get; set; }
}

public enum CodeHost
{
    Other,
    GitHub,
    Bitbucket,
    AzureDevOps,
}
