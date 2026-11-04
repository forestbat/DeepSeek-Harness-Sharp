using Dsh.Runtime;

namespace Dsh.Workspace;

/** Publishes the active backend and retires its owned connections when either plugin unloads. */
public sealed class WorkspaceService : IDisposable
{
    public const string ServiceName = "workspace";

    private readonly object _gate = new();
    private Registration? _registration;
    private bool _disposed;

    /** Ownership transfers only on success; the registration belongs to the backend's activation. */
    public IDisposable Register(Context owner, IWorkspaceProvider provider)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registration is not null)
                throw new InvalidOperationException("A workspace backend is already registered.");
            var publication = owner.Provide(IWorkspaceProvider.ServiceName, provider);
            return _registration = new Registration(this, provider, publication);
        }
    }

    private void Retire(Registration registration)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_registration, registration))
                _registration = null;
        }
    }

    public void Dispose()
    {
        Registration? registration;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            registration = _registration;
            _registration = null;
        }
        registration?.Dispose();
    }

    private sealed class Registration(WorkspaceService owner, IWorkspaceProvider provider, EffectHandle publication) : IDisposable
    {
        private IWorkspaceProvider? _provider = provider;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _provider, null);
            if (current is null) return;
            try
            {
                publication.DisposeAsync().GetAwaiter().GetResult();
            }
            finally
            {
                try { current.Dispose(); }
                finally { owner.Retire(this); }
            }
        }
    }
}
