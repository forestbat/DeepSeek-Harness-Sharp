namespace Dsh.Boot;

public sealed record HarnessHome(string Root)
{
    public static HarnessHome Resolve(string? explicitPath = null)
    {
        var root = explicitPath
            ?? Environment.GetEnvironmentVariable("DSH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return new HarnessHome(Path.GetFullPath(root));
    }

    public string SessionsPath => SubPath("sessions");

    public string ProfilesPath => SubPath("profiles");

    public string AgentPresetsPath => SubPath(".agent-presets");

    public string SubPath(string segment) => Path.Combine(Root, segment);

    public void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SessionsPath);
    }
}
