namespace TechTeaStudio.Injector;

/// <summary>
/// A built dependency injection container. The container itself acts as the root
/// <see cref="IResolver"/>; transient and singleton services may be resolved directly from it,
/// scoped services must be resolved from a <see cref="IScope"/> created via <see cref="CreateScope"/>.
/// </summary>
public interface IContainer : IResolver, IDisposable, IAsyncDisposable
{
    /// <summary>Creates a fresh resolution scope.</summary>
    IScope CreateScope();

    /// <summary>
    /// Returns <c>true</c> when at least one (non-decorator) registration of <typeparamref name="T"/>
    /// exists — either a closed registration or, for a constructed generic, a matching open-generic
    /// registration. Synthetic resolutions such as <c>Lazy&lt;T&gt;</c> / <c>Func&lt;T&gt;</c> and the
    /// built-in <see cref="IResolver"/> / <see cref="IScope"/> / <see cref="IContainer"/> services are
    /// not reported as registered.
    /// </summary>
    /// <typeparam name="T">Service type to probe.</typeparam>
    bool IsRegistered<T>();

    /// <summary>
    /// Returns <c>true</c> when at least one (non-decorator) registration of
    /// <paramref name="serviceType"/> exists — either a closed registration or, for a constructed
    /// generic, a matching open-generic registration.
    /// </summary>
    /// <param name="serviceType">Service type to probe.</param>
    bool IsRegistered(Type serviceType);
}
