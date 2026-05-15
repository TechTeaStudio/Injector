namespace TechTeaStudio.Injector;

/// <summary>
/// Resolves services out of a container or one of its scopes.
/// </summary>
public interface IResolver
{
    /// <summary>Resolves a single instance of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Service type previously registered with the container.</typeparam>
    T Resolve<T>();

    /// <summary>Resolves a single instance of <paramref name="serviceType"/>.</summary>
    /// <param name="serviceType">Service type previously registered with the container.</param>
    object Resolve(Type serviceType);

    /// <summary>Resolves every registration of <typeparamref name="T"/> in registration order.</summary>
    /// <typeparam name="T">Service type previously registered (possibly multiple times) with the container.</typeparam>
    IEnumerable<T> ResolveAll<T>();

    /// <summary>Resolves every registration of <paramref name="serviceType"/> in registration order.</summary>
    /// <param name="serviceType">Service type previously registered (possibly multiple times) with the container.</param>
    IEnumerable<object> ResolveAll(Type serviceType);

    /// <summary>
    /// Resolves a dictionary of keyed registrations for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="TKey">Key type used at registration time (commonly <see cref="string"/> or an enum).</typeparam>
    /// <typeparam name="T">Service type registered with keys.</typeparam>
    IDictionary<TKey, T> ResolveDictionary<TKey, T>() where TKey : notnull;
}
