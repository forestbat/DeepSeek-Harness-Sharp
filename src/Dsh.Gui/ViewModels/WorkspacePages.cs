using System.Collections.ObjectModel;
using Avalonia.Media;

namespace Dsh.Gui.ViewModels;

/** What a contributed page opens with: the machine and project of the session in view (null on the dashboard). */
public sealed record WorkspacePageContext(string? Machine, string? ProjectPath);

/**
 * A page another module contributes to the workspace navigation, such as files, Git or machine management.
 * The factory returns the page content: a control, or a view model for which the contributing module registers a data template.
 */
public sealed class WorkspacePageEntry(string id, string label, Geometry icon, Func<WorkspacePageContext, object?> factory)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public Geometry Icon { get; } = icon;

    internal Action<object>? DetachContent { get; init; }

    public object? Create(WorkspacePageContext context) => factory(context);
}

/**
 * The extension point for workspace pages: entries appear in the navigation once a module registers them, in registration order.
 * The reserved ids name the pages the workspace expects (files and editing, Git, machine management); nothing is shown for an id until it is registered.
 */
public sealed class WorkspacePages
{
    public const string Files = "files";
    public const string Git = "git";
    public const string Machines = "machines";

    private readonly Dictionary<string, WorkspacePageEntry> _defaults = new(StringComparer.Ordinal);

    public ObservableCollection<WorkspacePageEntry> Available { get; } = [];

    /** Adds the page or replaces the one registered under the same id. */
    public void Register(WorkspacePageEntry entry)
    {
        var existing = Available.FirstOrDefault(item => item.Id == entry.Id);
        if (existing is null)
            Available.Add(entry);
        else
            Available[Available.IndexOf(existing)] = entry;
    }

    /** Inserts a default host page at its canonical slot unless a plugin already supplied that id. */
    public void RegisterDefault(int index, WorkspacePageEntry entry)
    {
        _defaults[entry.Id] = entry;
        if (Available.Any(item => item.Id == entry.Id))
            return;
        Available.Insert(Math.Clamp(index, 0, Available.Count), entry);
    }

    internal WorkspacePageEntry? FindDefault(string id) => _defaults.GetValueOrDefault(id);

    /** Removes the contributed page with the given id when its owning plugin deactivates. */
    public bool Remove(string id)
    {
        var existing = Available.FirstOrDefault(item => item.Id == id);
        return existing is not null && Available.Remove(existing);
    }
}
