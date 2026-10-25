namespace Dsh.Fleet;

/** Where the release archive is downloaded during automatic deployment. */
public static class DownloadVia
{
    /** Let the remote download first, fall back to downloading locally and uploading with scp. */
    public const string Auto = "auto";

    /** Download locally and upload with scp. */
    public const string Client = "client";

    /** Only let the remote download. */
    public const string Server = "server";
}

/**
 * Automatic remote deployment preferences, shared by every SSH machine.
 * ReleaseVersion is "latest" or a tag (with or without the v prefix); ServerProxy is only used for remote deployment downloads.
 */
public sealed record DeploySettings
{
    /** The default release source (same as scripts/install.sh). */
    public const string DefaultReleaseBaseUrl = "https://github.com/forestbat/DeepSeek-Harness-Sharp/releases";

    public const string LatestVersion = "latest";

    public bool AutoDeploy { get; init; } = true;

    public string ReleaseBaseUrl { get; init; } = DefaultReleaseBaseUrl;

    public string ReleaseVersion { get; init; } = LatestVersion;

    public string Via { get; init; } = DownloadVia.Auto;

    public string? ServerProxy { get; init; }
}

/** The persisted fleet configuration: the SSH and extra local machines (the default local machine is implicit) and the deployment preferences. */
public sealed record FleetConfig(IReadOnlyList<MachineSpec> Machines, DeploySettings Deploy)
{
    public static FleetConfig Empty { get; } = new([], new DeploySettings());

    /** The implicit local machine followed by the configured ones. */
    public IReadOnlyList<MachineSpec> AllMachines() => [MachineSpec.Local(), .. Machines];

    public MachineSpec? Find(string name) => AllMachines().FirstOrDefault(machine => string.Equals(machine.Name, name, StringComparison.Ordinal));
}
