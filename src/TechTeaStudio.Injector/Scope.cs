namespace TechTeaStudio.Injector;

/// <summary>
/// A unit of scoped resolution. Scoped registrations resolve to one instance per scope. Disposing
/// the scope releases every <see cref="IDisposable"/> instance the scope owns, in reverse creation
/// order. Disposal is idempotent; further resolves throw <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class Scope : IScope
{
    private readonly Container _container;
    private readonly Dictionary<Registration, object> _scoped = new();
    private readonly List<IDisposable> _owned = new();
    private readonly object _lock = new();
    private bool _disposed;

    public Scope(Container container)
    {
        _container = container;
    }

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
                if (!ReferenceEquals(raceWinner, created) && created is IDisposable d)
                {
                    try { d.Dispose(); } catch { }
                }
                return raceWinner;
            }
            _scoped[reg] = created;
            if (created is IDisposable disp) _owned.Add(disp);
            return created;
        }
    }

    internal void TrackTransient(IDisposable disposable)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // The scope has already been disposed; just dispose the transient ourselves.
                try { disposable.Dispose(); } catch { }
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
        List<IDisposable> snapshot;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = new List<IDisposable>(_owned);
            _owned.Clear();
            _scoped.Clear();
        }
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            try { snapshot[i].Dispose(); } catch { }
        }
    }
}
