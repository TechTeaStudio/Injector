using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection;

/// <summary>
/// Adapter that exposes an <see cref="IScope"/> as a MEDI <see cref="IServiceScope"/>. Disposing the
/// scope disposes its underlying <see cref="IScope"/>, which in turn disposes every scoped /
/// transient instance the scope owns (in reverse creation order).
/// </summary>
public sealed class InjectorServiceScope : IServiceScope, IAsyncDisposable
{
    private readonly IScope _scope;
    private readonly ScopedInjectorServiceProvider _provider;
    private bool _disposed;

    /// <summary>
    /// Creates a scope adapter over <paramref name="scope"/>.
    /// </summary>
    /// <param name="scope">Scope to wrap.</param>
    /// <param name="rootContainer">Root container, used for non-scoped fallback resolution.</param>
    public InjectorServiceScope(IScope scope, IContainer rootContainer)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (rootContainer is null) throw new ArgumentNullException(nameof(rootContainer));
        _provider = new ScopedInjectorServiceProvider(_scope, rootContainer);
    }

    /// <inheritdoc />
    public IServiceProvider ServiceProvider => _provider;

    internal IScope UnderlyingScope => _scope;

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

/// <summary>
/// IServiceProvider implementation that routes resolution through an underlying <see cref="IScope"/>.
/// Used by <see cref="InjectorServiceScope"/> so scoped services resolve correctly under MEDI.
/// </summary>
internal sealed class ScopedInjectorServiceProvider :
    IServiceProvider,
    IServiceProviderIsService,
    IServiceProviderIsKeyedService,
    IKeyedServiceProvider
{
    private readonly IScope _scope;
    private readonly IContainer? _root;

    public ScopedInjectorServiceProvider(IScope scope, IContainer? root)
    {
        _scope = scope;
        _root = root;
    }

    internal IScope Scope => _scope;

    public object? GetService(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));

        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceProviderIsService)) return this;
        if (serviceType == typeof(IServiceProviderIsKeyedService)) return this;
        if (serviceType == typeof(IKeyedServiceProvider)) return this;
        if (serviceType == typeof(IServiceScopeFactory))
        {
            // Within a scope, the scope factory should be reachable. We re-use the root container's
            // factory; nested scopes off this factory will still parent at the root.
            if (_root is null)
            {
                throw new InvalidOperationException(
                    "IServiceScopeFactory is only available on a scope created via a root container.");
            }
            return new InjectorServiceScopeFactory(_root);
        }

        try
        {
            return _scope.Resolve(serviceType);
        }
        catch (ServiceNotRegisteredException)
        {
            return null;
        }
    }

    public bool IsService(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (serviceType == typeof(IServiceScopeFactory)) return _root is not null;
        if (_root is null) return false;
        return InjectorServiceProvider.IsServiceCore(_root, serviceType);
    }

    public bool IsKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (_root is null) return false;
        if (serviceKey is null) return InjectorServiceProvider.IsServiceCore(_root, serviceType);
        return KeyedResolutionHelpers.HasKeyedRegistration(_root, serviceType, serviceKey);
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        return KeyedResolutionHelpers.TryGetKeyedService(_root, _scope, serviceType, serviceKey);
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        var svc = GetKeyedService(serviceType, serviceKey);
        if (svc is null)
        {
            throw new InvalidOperationException(
                $"No service for type '{serviceType.FullName}' with key '{serviceKey}' has been registered.");
        }
        return svc;
    }
}

/// <summary>
/// IServiceProvider wrapper around a raw <see cref="IResolver"/>. Used to bridge MEDI factory
/// delegates that take <see cref="IServiceProvider"/> to the Injector's <see cref="IResolver"/>.
/// </summary>
internal sealed class ResolverServiceProvider : IServiceProvider, IKeyedServiceProvider
{
    private readonly IResolver _resolver;

    public ResolverServiceProvider(IResolver resolver) => _resolver = resolver;

    public object? GetService(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (serviceType == typeof(IServiceProvider)) return this;
        try
        {
            return _resolver.Resolve(serviceType);
        }
        catch (ServiceNotRegisteredException)
        {
            return null;
        }
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        // We don't have access to a container here, only an IResolver. Best-effort: use
        // ResolveDictionary<object, T>() via reflection if available.
        return KeyedResolutionHelpers.TryGetKeyedServiceFromResolver(_resolver, serviceType, serviceKey);
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        var svc = GetKeyedService(serviceType, serviceKey);
        if (svc is null)
        {
            throw new InvalidOperationException(
                $"No service for type '{serviceType.FullName}' with key '{serviceKey}' has been registered.");
        }
        return svc;
    }
}
