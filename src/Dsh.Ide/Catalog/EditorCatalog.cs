using Dsh.Core;

namespace Dsh.Ide.Catalog;

public sealed class EditorCatalog : IEditorCatalog
{
    private readonly object gate = new();
    private readonly List<(long Token, EditorDescriptor Descriptor)> registrations = [];
    private readonly SemaphoreSlim detectionGate = new(1, 1);
    private readonly EditorDetector detector;
    private long nextToken;
    private long generation;
    private long cachedGeneration = -1;
    private IReadOnlyList<EditorInstallation> cached = [];

    public EditorCatalog(IEnumerable<EditorDescriptor> descriptors, EditorDetector? detector = null)
    {
        this.detector = detector ?? new();
        foreach (var descriptor in descriptors) Register(descriptor);
    }

    public IReadOnlyList<EditorDescriptor> Descriptors
    {
        get
        {
            lock (gate)
            {
                var latest = registrations.GroupBy(r => r.Descriptor.Id).Select(g => g.Last()).OrderBy(r => r.Token);
                return latest.Select(r => Snapshot(r.Descriptor)).ToArray();
            }
        }
    }
    public EditorDescriptor? Find(EditorId id)
    {
        lock (gate)
        {
            var descriptor = registrations.LastOrDefault(r => r.Descriptor.Id == id).Descriptor;
            return descriptor is null ? null : Snapshot(descriptor);
        }
    }
    public IDisposable Register(EditorDescriptor descriptor)
    {
        Validate(descriptor);
        lock (gate)
        {
            var token = ++nextToken;
            registrations.Add((token, Snapshot(descriptor)));
            generation++;
            return new Registration(this, token);
        }
    }
    public async ValueTask<IReadOnlyList<EditorInstallation>> DetectAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await detectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long current;
            lock (gate) current = generation;
            if (!refresh && cachedGeneration == current) return cached.ToArray();
            var descriptors = Descriptors;
            var detected = await Task.Run(() => descriptors.Select(d => detector.Detect(d, cancellationToken)).OfType<EditorInstallation>().ToArray(), cancellationToken).ConfigureAwait(false);
            cached = detected;
            cachedGeneration = current;
            return detected.ToArray();
        }
        finally { detectionGate.Release(); }
    }
    private void Remove(long token)
    {
        lock (gate)
        {
            registrations.RemoveAll(r => r.Token == token);
            generation++;
        }
    }
    private sealed class Registration(EditorCatalog owner, long token) : IDisposable
    {
        private EditorCatalog? catalog = owner;
        public void Dispose() => Interlocked.Exchange(ref catalog, null)?.Remove(token);
    }
    private static EditorDescriptor Snapshot(EditorDescriptor d) => d with
    {
        Actions = d.Actions.ToDictionary(p => p.Key, p => p.Value.ToArray()),
        RemoteActions = d.RemoteActions.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
        DiscoveryCommands = d.DiscoveryCommands.Select(command => command with { Arguments = command.Arguments.ToArray() }).ToArray(),
        Environment = d.Environment.ToDictionary(p => p.Key, p => p.Value),
        When = d.When with { Workspace = d.When.Workspace.ToArray(), Extensions = d.When.Extensions.ToArray(), Globs = d.When.Globs.ToArray() },
        ExecutableCandidates = d.ExecutableCandidates.ToArray(), RemoteExecutableCandidates = d.RemoteExecutableCandidates.ToArray(), BundleNames = d.BundleNames.ToArray(),
        ReuseArguments = d.ReuseArguments.ToArray(), NewWindowArguments = d.NewWindowArguments.ToArray(),
        BackgroundArguments = d.BackgroundArguments.ToArray(), FocusArguments = d.FocusArguments.ToArray(),
    };
    private static void Validate(EditorDescriptor d)
    {
        if (string.IsNullOrWhiteSpace(d.Id.Value) || d.Id.Value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Editor ID must be a stable ASCII identifier.");
        if (string.IsNullOrWhiteSpace(d.Command) || d.Command.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Editor command must name an executable, not a shell command.");
        foreach (var key in d.Environment.Keys)
            if (key.Length == 0 || key.IndexOfAny(['=', '\0']) >= 0) throw new ArgumentException("Invalid editor environment key.");
        foreach (var action in d.Actions.Concat(d.RemoteActions))
            foreach (var argument in action.Value) Launch.EditorTemplates.Validate(argument);
        foreach (var argument in d.ReuseArguments.Concat(d.NewWindowArguments).Concat(d.BackgroundArguments).Concat(d.FocusArguments))
            Launch.EditorTemplates.Validate(argument);
        if (d.WorkingDirectory is not null) Launch.EditorTemplates.Validate(d.WorkingDirectory);
        foreach (var value in d.Environment.Values) Launch.EditorTemplates.Validate(value);
    }
}
