namespace TechTeaStudio.Injector;

/// <summary>
/// A unit of scoped resolution. Scoped registrations resolve to one instance per scope. Disposing
/// the scope releases every <see cref="IDisposable"/> / <see cref="IAsyncDisposable"/> instance the
/// scope owns, in reverse creation order. Disposal is idempotent; further resolves throw
/// <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class Scope : IScope
{
    private readonly Container _container;
    private readonly Scope? _parent;
    private readonly ChildScopeRegistry? _extra;
    private readonly Dictionary<Registration, object> _scoped = new();
    private readonly List<object> _owned = new();
    private readonly object _lock = new();
    private bool _disposed;

    public Scope(Container container)
    {
        _container = container;
    }

    /// <summary>Creates a child scope with extra (child-only) registrations.</summary>
    internal Scope(Container container, Scope parent, ChildScopeRegistry extra)
    {
        _container = container;
        _parent = parent;
        _extra = extra;
    }

    internal Container Container => _container;
    internal Scope? Parent => _parent;
    internal ChildScopeRegistry? Extra => _extra;

    public T Resolve<T>() => (T)Resolve(typeof(T));

    public object Resolve(Type serviceType)
    {
        EnsureNotDisposed();
        return _container.ResolveInternal(serviceType, this);
    }

    public IEnumerable<T> ResolveAll<T>() => ResolveAll(typeof(T)).Cast<T>();

    public IEnumerable<object> ResolveAll(Type serviceType)
    {
        EnsureNotDisposed();
        return _container.ResolveAllInternal(serviceType, this);
    }

    public IDictionary<TKey, T> ResolveDictionary<TKey, T>() where TKey : notnull
    {
        EnsureNotDisposed();
        return _container.ResolveDictionaryInternal<TKey, T>(this);
    }

    /// <summary>
    /// Begins a nested lifetime scope whose configuration overrides the parent. The child scope
    /// sees every parent registration plus whatever <paramref name="configure"/> registers.
    /// </summary>
    public IScope BeginLifetimeScope(Action<IContainerBuilder> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        EnsureNotDisposed();
        var childBuilder = new ContainerBuilder();
        configure(childBuilder);
        var registry = ChildScopeRegistry.From(childBuilder.Registrations);
        return new Scope(_container, this, registry);
    }

    internal object GetOrCreateScoped(Registration reg, Func<object> factory)
    {
        EnsureNotDisposed();
        lock (_lock)
        {
            if (_scoped.TryGetValue(reg, out var existing)) return existing;
        }

        var created = factory();

        lock (_lock)
        {
            if (_scoped.TryGetValue(reg, out var raceWinner))
            {
                if (!ReferenceEquals(raceWinner, created) && DisposalHelpers.IsTrackable(created))
                {
                    DisposalHelpers.DisposeSync(created);
                }
                return raceWinner;
            }
            _scoped[reg] = created;
            if (DisposalHelpers.IsTrackable(created)) _owned.Add(created);
            return created;
        }
    }

    internal void TrackTransient(object disposable)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // The scope has already been disposed; just dispose the transient ourselves.
                DisposalHelpers.DisposeSync(disposable);
                return;
            }
            _owned.Add(disposable);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Scope));
    }

    public void Dispose()
    {
        List<object> snapshot;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = new List<object>(_owned);
            _owned.Clear();
            _scoped.Clear();
        }
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            DisposalHelpers.DisposeSync(snapshot[i]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<object> snapshot;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = new List<object>(_owned);
            _owned.Clear();
            _scoped.Clear();
        }
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            await DisposalHelpers.DisposeAsyncSafe(snapshot[i]).ConfigureAwait(false);
        }
    }
}
