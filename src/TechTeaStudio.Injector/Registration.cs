namespace TechTeaStudio.Injector;

/// <summary>
/// Internal metadata describing a single registration. Built by <see cref="ContainerBuilder"/>
/// and consumed by the resolution engine.
/// </summary>
internal sealed class Registration
{
    /// <summary>Primary service contract for this registration.</summary>
    public Type ServiceType { get; }

    /// <summary>Implementation type, when registered by type. <c>null</c> for factory / instance registrations.</summary>
    public Type? ImplementationType { get; private set; }

    /// <summary>Pre-built instance (only set by <see cref="ContainerBuilder.RegisterInstance"/>).</summary>
    public object? Instance { get; private set; }

    /// <summary>User-supplied factory delegate (only set by the factory <c>Register</c> overload).</summary>
    public Func<IResolver, object>? Factory { get; private set; }

    /// <summary>Selected lifetime. Mutable until the container is built.</summary>
    public Lifetime Lifetime { get; private set; }

    /// <summary>Optional lookup key, used by <see cref="IResolver.ResolveDictionary{TKey, T}"/>.</summary>
    public object? Key { get; private set; }

    /// <summary>Additional service contracts this registration also satisfies.</summary>
    public List<Type> Aliases { get; } = new();

    /// <summary>Order in which the registration was added (used for decorator/collection ordering).</summary>
    public int Order { get; }

    /// <summary>True for registrations created internally as decorator wrappers.</summary>
    public bool IsDecorator { get; private set; }

    /// <summary>For decorator registrations: the decorator implementation type.</summary>
    public Type? DecoratorType { get; private set; }

    private Registration(Type serviceType, Lifetime lifetime, int order)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
        Order = order;
    }

    /// <summary>Constructs a type-based registration.</summary>
    public static Registration ForType(Type serviceType, Type implementationType, Lifetime lifetime, int order)
        => new(serviceType, lifetime, order) { ImplementationType = implementationType };

    /// <summary>Constructs a factory-based registration.</summary>
    public static Registration ForFactory(Type serviceType, Func<IResolver, object> factory, Lifetime lifetime, int order)
        => new(serviceType, lifetime, order) { Factory = factory };

    /// <summary>Constructs an instance-based registration (always singleton).</summary>
    public static Registration ForInstance(Type serviceType, object instance, int order)
        => new(serviceType, Lifetime.Singleton, order) { Instance = instance };

    /// <summary>Constructs a decorator registration.</summary>
    public static Registration ForDecorator(Type serviceType, Type decoratorType, int order)
        => new(serviceType, Lifetime.Transient, order) { DecoratorType = decoratorType, IsDecorator = true };

    /// <summary>Sets the lifetime.</summary>
    public Registration SetLifetime(Lifetime lifetime) { Lifetime = lifetime; return this; }

    /// <summary>Sets the registration key.</summary>
    public Registration SetKey(object key) { Key = key; return this; }
}
