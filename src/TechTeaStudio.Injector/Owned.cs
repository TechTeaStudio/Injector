namespace TechTeaStudio.Injector;

/// <summary>
/// A wrapper representing transferred ownership of a resolved service. Disposing the
/// <see cref="Owned{T}"/> releases the inner instance together with the inner scope that the
/// container created to house it. Useful for short-lived, deterministically-released services
/// inside a larger long-lived scope.
/// </summary>
/// <typeparam name="T">Service type contained inside this owner.</typeparam>
public sealed class Owned<T> : IDisposable, IAsyncDisposable
{
    private readonly IScope _scope;
    private bool _disposed;

    /// <summary>The resolved value. Always non-null while the owner has not been disposed.</summary>
    public T Value { get; }

    /// <summary>
    /// Constructs a new <see cref="Owned{T}"/>. Should only be called by the injector itself.
    /// </summary>
    /// <param name="value">Resolved inner value.</param>
    /// <param name="scope">Inner scope that owns disposables created during the resolve.</param>
    public Owned(T value, IScope scope)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        Value = value;
        _scope = scope;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scope.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _scope.DisposeAsync().ConfigureAwait(false);
    }
}
