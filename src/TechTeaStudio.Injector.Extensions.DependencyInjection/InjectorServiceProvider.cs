using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection;

/// <summary>
/// Adapter that exposes an <see cref="IContainer"/> as the full MEDI surface:
/// <see cref="IServiceProvider"/>, <see cref="IServiceProviderIsService"/>,
/// <see cref="IKeyedServiceProvider"/>, <see cref="IDisposable"/>, <see cref="IAsyncDisposable"/>.
/// Returned by <see cref="ServiceCollectionExtensions.AsServiceProvider(IContainer)"/>.
/// </summary>
public sealed class InjectorServiceProvider :
    IServiceProvider,
    IServiceProviderIsService,
    IServiceProviderIsKeyedService,
    IKeyedServiceProvider,
    IDisposable,
    IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly bool _ownsContainer;
    private bool _disposed;

    /// <summary>
    /// Creates a provider that adapts <paramref name="container"/>. The provider does NOT take
    /// ownership of the container; the caller is responsible for disposing the underlying
    /// container. Disposing the provider itself is a no-op for the wrapped container.
    /// </summary>
    /// <param name="container">Container to wrap.</param>
    public InjectorServiceProvider(IContainer container)
        : this(container, ownsContainer: false)
    {
    }

    internal InjectorServiceProvider(IContainer container, bool ownsContainer)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _ownsContainer = ownsContainer;
    }

    internal IContainer Container => _container;

    /// <inheritdoc />
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "Implements IServiceProvider.GetService, which carries no trimming annotations upstream, so the requirement cannot be propagated. The wrapped Injector container is not trim-safe; this is documented on the package and on the core's [RequiresUnreferencedCode] entry points.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Implements IServiceProvider.GetService, which carries no AOT annotations upstream, so the requirement cannot be propagated. The wrapped Injector container compiles expression trees and is not AOT-safe; this is documented on the package and on the core's [RequiresDynamicCode] entry points.")]
    public object? GetService(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        EnsureNotDisposed();

        // MEDI intrinsics: every IServiceProvider also resolves IServiceProvider, IServiceScopeFactory.
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceScopeFactory)) return new InjectorServiceScopeFactory(_container);
        if (serviceType == typeof(IServiceProviderIsService)) return this;
        if (serviceType == typeof(IServiceProviderIsKeyedService)) return this;
        if (serviceType == typeof(IKeyedServiceProvider)) return this;

        try
        {
            return _container.Resolve(serviceType);
        }
        catch (ServiceNotRegisteredException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsService(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        EnsureNotDisposed();
        return IsServiceCore(_container, serviceType);
    }

    /// <inheritdoc />
    public bool IsKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        EnsureNotDisposed();
        if (serviceKey is null) return IsServiceCore(_container, serviceType);
        return KeyedResolutionHelpers.HasKeyedRegistration(_container, serviceType, serviceKey);
    }

    /// <summary>
    /// Shared <c>IsService</c> logic. Mirrors MEDI's behaviour: well-known intrinsics, any
    /// registered service type, plus the synthetic collection / Lazy / Func / Owned wrappers that
    /// the Injector auto-resolves when their inner element type is registered.
    /// </summary>
    internal static bool IsServiceCore(IContainer container, Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider)) return true;
        if (serviceType == typeof(IServiceScopeFactory)) return true;
        if (serviceType == typeof(IServiceProviderIsService)) return true;
        if (serviceType == typeof(IServiceProviderIsKeyedService)) return true;
        if (serviceType == typeof(IKeyedServiceProvider)) return true;

        if (KeyedResolutionHelpers.HasAnyRegistration(container, serviceType)) return true;

        // Synthetic wrappers: IEnumerable<T> / IReadOnlyCollection<T> / IReadOnlyList<T> /
        // ICollection<T> / IList<T> are satisfied whenever the element T is registered (MEDI
        // returns true even for an empty enumerable, matching the spec).
        if (serviceType.IsGenericType)
        {
            var def = serviceType.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyCollection<>)
                || def == typeof(IReadOnlyList<>)
                || def == typeof(ICollection<>)
                || def == typeof(IList<>))
            {
                return true;
            }

            // Lazy<T> / Func<T> / Owned<T> resolve when T resolves.
            var args = serviceType.GetGenericArguments();
            if (def == typeof(Lazy<>) || def == typeof(Owned<>))
            {
                return IsServiceCore(container, args[0]);
            }
            if (def.FullName is { } name && name.StartsWith("System.Func`", StringComparison.Ordinal))
            {
                // Func<T>: T must be a service. Func<P,...,T>: parameterized factory, always synthesizable.
                if (args.Length == 1) return IsServiceCore(container, args[0]);
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "Implements IKeyedServiceProvider.GetKeyedService, which carries no trimming annotations upstream. The wrapped Injector container is not trim-safe; documented on the package and on the core's [RequiresUnreferencedCode] entry points.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Implements IKeyedServiceProvider.GetKeyedService, which carries no AOT annotations upstream. The wrapped Injector container compiles expression trees and is not AOT-safe; documented on the package and on the core's [RequiresDynamicCode] entry points.")]
    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        EnsureNotDisposed();
        return KeyedResolutionHelpers.TryGetKeyedService(_container, scope: null, serviceType, serviceKey);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsContainer) _container.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsContainer)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InjectorServiceProvider));
    }
}
