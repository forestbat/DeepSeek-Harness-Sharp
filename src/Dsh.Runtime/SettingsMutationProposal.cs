namespace Dsh.Runtime;

public readonly record struct SettingsMutationProposal(string Before, string After);

public delegate Task<bool> SettingsCommit(string path, Func<string, SettingsMutationProposal?> prepare,
    Func<string, SettingsMutationProposal, string, CancellationToken, Task> commit);
