using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection;

/// <summary>
/// Typed bridge into <see cref="Container"/>'s introspection surface. Uses only the
/// <c>internal</c> hooks exposed by the core library (via <c>InternalsVisibleTo</c>);
/// no reflection or private-field access is involved.
/// </summary>
internal static class KeyedResolutionHelpers
{
    /// <summary>
    /// Returns true if <paramref name="container"/> has at least one non-decorator registration that
    /// can satisfy <paramref name="serviceType"/>. Covers closed and (via the open-generic table)
    /// constructed-generic types.
    /// </summary>
    public static bool HasAnyRegistration(IContainer container, Type serviceType)
    {
        if (container is not Container c) return false;
        return c.IsRegisteredInternal(serviceType);
    }

    /// <summary>
    /// Returns true if <paramref name="container"/> has a non-decorator registration of
    /// <paramref name="serviceType"/> bound to <paramref name="serviceKey"/>.
    /// </summary>
    public static bool HasKeyedRegistration(IContainer container, Type serviceType, object serviceKey)
    {
        if (container is not Container c) return false;
        return c.IsRegisteredInternal(serviceType, serviceKey);
    }

    /// <summary>
    /// Tries to resolve a keyed service from the container or scope. Returns null if no registration
    /// of <paramref name="serviceType"/> with matching <paramref name="serviceKey"/> exists.
    /// </summary>
    public static object? TryGetKeyedService(IContainer? container, IScope? scope, Type serviceType, object? serviceKey)
    {
        if (serviceKey is null) return null;
        if (container is not Container c) return null;
        Scope? typedScope = scope as Scope;
        return c.ResolveKeyed(serviceType, serviceKey, typedScope);
    }

    /// <summary>
    /// Best-effort keyed retrieval given only an <see cref="IResolver"/> (e.g. inside a factory
    /// delegate). Routes through <see cref="Container.ResolveKeyed(Type, object, Scope?)"/> when the
    /// resolver is one of the known concrete types; otherwise returns <c>null</c>.
    /// </summary>
    public static object? TryGetKeyedServiceFromResolver(IResolver resolver, Type serviceType, object? serviceKey)
    {
        if (serviceKey is null) return null;
        switch (resolver)
        {
            case Scope scope:
                return scope.Container.ResolveKeyed(serviceType, serviceKey, scope);
            case Container container:
                return container.ResolveKeyed(serviceType, serviceKey, scope: null);
            default:
                return null;
        }
    }
}
