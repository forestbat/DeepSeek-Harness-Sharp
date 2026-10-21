namespace Dsh.Core;

public sealed record ExternalEditorLaunchInfo(
    string Editor,
    string Command,
    IReadOnlyList<string> Arguments,
    int? ProcessId);

/** Optional capability supplied by the external-editor tool plugin. */
public interface IExternalEditorLauncher
{
    const string ServiceName = "externalEditorLauncher";

    ExternalEditorLaunchInfo Launch(
        string path,
        string? editorName = null,
        int? line = null,
        int? column = null);
}
