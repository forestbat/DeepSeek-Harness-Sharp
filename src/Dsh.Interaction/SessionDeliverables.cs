using Dsh.Core;
using Dsh.Llm;
using Dsh.Plugins;

namespace Dsh.Interaction;

/** A file the model declared as a final deliverable; the file stays at its source path and is never copied. */
public sealed record PresentedFile(string Path, string? Description = null);

/** One successful present call of the owning session, with absolute file paths. */
public sealed record DeliverablesPresentedPayload(int Turn, ToolCallId CallId, IReadOnlyList<PresentedFile> Files) : SessionEventPayload
{
    public const string EventType = "deliverables/presented";

    public override string Type => EventType;
}

/** The session deliverable list is folded from the event log, so resume, truncation and forks follow the log. */
public static class SessionDeliverables
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /** Unique files in first-presented order; a later declaration of the same path replaces its description. */
    public static IReadOnlyList<PresentedFile> Collect(IEnumerable<SessionEvent> events)
        => Collect(events
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<DeliverablesPresentedPayload>()
            .Select(presented => presented.Files));

    /** 同一套折叠规则的另一种输入: 每次"交付"的文件组(投影缓存里存的就是这个, 免得连事件一起存)。 */
    public static IReadOnlyList<PresentedFile> Collect(IEnumerable<IReadOnlyList<PresentedFile>> presentations)
    {
        var order = new List<string>();
        var latest = new Dictionary<string, PresentedFile>(PathComparer);
        foreach (var files in presentations)
        {
            foreach (var file in files)
            {
                if (latest.TryAdd(file.Path, file))
                    order.Add(file.Path);
                else
                    latest[file.Path] = file;
            }
        }
        return [.. order.Select(path => latest[path])];
    }
}

internal static class DeliverablesCodecRegistration
{
    // Registered at startup: a resumed log may carry deliverables even when the present tool plugin is disabled.
    [DshPluginInitializer]
    internal static void Register()
        => SessionEventCodec.Register<DeliverablesPresentedPayload>(DeliverablesPresentedPayload.EventType);
}
