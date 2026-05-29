using System.Diagnostics.CodeAnalysis;

namespace TechTeaStudio.Injector;

/// <summary>
/// Resolves services out of a container or one of its scopes.
/// </summary>
public interface IResolver
{
    /// <summary>Resolves a single instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Service type previously registered with the container.</typeparam>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    T Resolve<T>();

    /// <summary>Resolves a single instance of <paramref name="serviceType"/>.</summary>
    /// <param name="serviceType">Service type previously registered with the container.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    object Resolve(Type serviceType);

    /// <summary>Resolves every registration of <typeparamref name="T"/> in registration order.</summary>
    /// <typeparam name="T">Service type previously registered (possibly multiple times) with the container.</typeparam>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IEnumerable<T> ResolveAll<T>();

    /// <summary>Resolves every registration of <paramref name="serviceType"/> in registration order.</summary>
    /// <param name="serviceType">Service type previously registered (possibly multiple times) with the container.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IEnumerable<object> ResolveAll(Type serviceType);

    /// <summary>
    /// Resolves a dictionary of keyed registrations for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="TKey">Key type used at registration time (commonly <see cref="string"/> or an enum).</typeparam>
    /// <typeparam name="T">Service type registered with keys.</typeparam>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IDictionary<TKey, T> ResolveDictionary<TKey, T>() where TKey : notnull;

    /// <summary>
    /// Resolves the registration of <typeparamref name="T"/> tagged with <paramref name="key"/>.
    /// </summary>
    /// <typeparam name="T">Service type registered with the supplied key.</typeparam>
    /// <param name="key">Lookup key supplied at registration time via <c>WithKey</c>.</param>
    /// <exception cref="ServiceNotRegisteredException">No registration of <typeparamref name="T"/> carries <paramref name="key"/>.</exception>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    T ResolveKeyed<T>(object key);

    /// <summary>
    /// Resolves the registration of <paramref name="serviceType"/> tagged with <paramref name="key"/>.
    /// </summary>
    /// <param name="serviceType">Service type registered with the supplied key.</param>
    /// <param name="key">Lookup key supplied at registration time via <c>WithKey</c>.</param>
    /// <exception cref="ServiceNotRegisteredException">No registration of <paramref name="serviceType"/> carries <paramref name="key"/>.</exception>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    object ResolveKeyed(Type serviceType, object key);

    /// <summary>
    /// Attempts to resolve the registration of <typeparamref name="T"/> tagged with <paramref name="key"/>.
    /// Returns <c>false</c> (and a default <paramref name="value"/>) only when no matching keyed
    /// registration exists; a genuine construction failure still propagates.
    /// </summary>
    /// <typeparam name="T">Service type registered with the supplied key.</typeparam>
    /// <param name="key">Lookup key supplied at registration time via <c>WithKey</c>.</param>
    /// <param name="value">The resolved instance, or <c>default</c> when no keyed registration matched.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    bool TryResolveKeyed<T>(object key, out T value);

    /// <summary>
    /// Attempts to resolve <typeparamref name="T"/>. Returns <c>false</c> (and a default
    /// <paramref name="value"/>) only when <typeparamref name="T"/> is not registered at the top
    /// level; if the service is registered but its construction fails (for example a missing
    /// transitive dependency) the error still propagates.
    /// </summary>
    /// <typeparam name="T">Service type to resolve.</typeparam>
    /// <param name="value">The resolved instance, or <c>default</c> when <typeparamref name="T"/> is not registered.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    bool TryResolve<T>(out T value);

    /// <summary>
    /// Attempts to resolve <paramref name="serviceType"/>. Returns <c>false</c> (and a <c>null</c>
    /// <paramref name="value"/>) only when the service is not registered at the top level; a genuine
    /// construction failure still propagates.
    /// </summary>
    /// <param name="serviceType">Service type to resolve.</param>
    /// <param name="value">The resolved instance, or <c>null</c> when <paramref name="serviceType"/> is not registered.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    bool TryResolve(Type serviceType, out object? value);

    /// <summary>
    /// Resolves <typeparamref name="T"/> if it is registered, otherwise returns <c>default</c>.
    /// Mirrors <see cref="TryResolve{T}(out T)"/>: only a missing top-level registration yields
    /// <c>default</c>; a genuine construction failure still propagates.
    /// </summary>
    /// <typeparam name="T">Service type to resolve.</typeparam>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    T? ResolveOptional<T>();

    /// <summary>
    /// Resolves <paramref name="serviceType"/> if it is registered, otherwise returns <c>null</c>.
    /// Mirrors <see cref="TryResolve(Type, out object?)"/>: only a missing top-level registration
    /// yields <c>null</c>; a genuine construction failure still propagates.
    /// </summary>
    /// <param name="serviceType">Service type to resolve.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    object? ResolveOptional(Type serviceType);
}
