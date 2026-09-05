namespace Dsh.Skills;

public sealed record SkillFsTarget(string DisplayPath);

public sealed record SkillFsDirEntry(string Name, string Type, SkillFsTarget Target);

public sealed record SkillFsInfo(string Type, long Size);

public static class SkillFsEntryTypes
{
    public const string File = "file";
    public const string Directory = "directory";
    public const string Other = "other";
}

public sealed class SkillFsException : Exception
{
    public const string NotFound = "FS_NOT_FOUND";
    public const string NotDirectory = "FS_NOT_DIRECTORY";
    public const string NotText = "FS_NOT_TEXT";

    public SkillFsException(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    public bool IsAbsent => Code is NotFound or NotDirectory;
}

public interface ISkillFs
{
    Task<SkillFsTarget> Resolve(string path);

    Task<IReadOnlyList<SkillFsDirEntry>> ListDir(SkillFsTarget target);

    Task<SkillFsInfo?> Stat(SkillFsTarget target, CancellationToken signal);

    Task<string> ReadText(SkillFsTarget target, CancellationToken signal);
}
