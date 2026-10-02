using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TechTeaStudio.Injector;

/// <summary>
/// Default in-memory implementation of <see cref="IContainerBuilder"/>. Collects registrations,
/// then hands them off to the resolution engine when <see cref="Build()"/> is called.
/// </summary>
public sealed class ContainerBuilder : IContainerBuilder
{
    private readonly List<Registration> _registrations = new();
    private int _orderCounter;
    private bool _built;

    /// <summary>Internal access to the accumulated registrations (used by <see cref="Container"/>).</summary>
    internal IReadOnlyList<Registration> Registrations => _registrations;

    /// <inheritdoc />
    public IRegistrationBuilder Register<TService, TImpl>()
        where TImpl : class, TService
        where TService : class
    {
        EnsureNotBuilt();
        var reg = Registration.ForType(typeof(TService), typeof(TImpl), Lifetime.Transient, _orderCounter++);
        _registrations.Add(reg);
        return new RegistrationBuilder(this, reg);
    }

    /// <inheritdoc />
    public IRegistrationBuilder Register<TService>() where TService : class
    {
        EnsureNotBuilt();
        var reg = Registration.ForType(typeof(TService), typeof(TService), Lifetime.Transient, _orderCounter++);
        _registrations.Add(reg);
        return new RegistrationBuilder(this, reg);
    }

    /// <inheritdoc />
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public IRegistrationBuilder Register(Type serviceType, Type implementationType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (implementationType is null) throw new ArgumentNullException(nameof(implementationType));
        EnsureNotBuilt();

        if (serviceType.IsGenericTypeDefinition ^ implementationType.IsGenericTypeDefinition)
        {
            throw new RegistrationException(
                $"Cannot register '{serviceType.FullName}' against '{implementationType.FullName}': both must be open generic type definitions, or neither.");
        }

        var reg = Registration.ForType(serviceType, implementationType, Lifetime.Transient, _orderCounter++);
        _registrations.Add(reg);
        return new RegistrationBuilder(this, reg);
    }

    /// <inheritdoc />
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public IRegistrationBuilder Register(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        return Register(serviceType, serviceType);
    }

    /// <inheritdoc />
    public IRegistrationBuilder Register<TService>(Func<IResolver, TService> factory) where TService : class
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        EnsureNotBuilt();
        var reg = Registration.ForFactory(typeof(TService), r => factory(r), Lifetime.Transient, _orderCounter++);
        _registrations.Add(reg);
        return new RegistrationBuilder(this, reg);
    }

    /// <inheritdoc />
    public IRegistrationBuilder RegisterInstance<TService>(TService instance) where TService : class
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        EnsureNotBuilt();
        var reg = Registration.ForInstance(typeof(TService), instance, _orderCounter++);
        _registrations.Add(reg);
        return new RegistrationBuilder(this, reg);
    }

    /// <inheritdoc />
    public IContainerBuilder RegisterDecorator<TService, TDecorator>()
        where TDecorator : class, TService
        where TService : class
    {
        EnsureNotBuilt();
        var reg = Registration.ForDecorator(typeof(TService), typeof(TDecorator), _orderCounter++);
        _registrations.Add(reg);
        return this;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public IContainerBuilder RegisterDecorator(Type serviceType, Type decoratorType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (decoratorType is null) throw new ArgumentNullException(nameof(decoratorType));
        EnsureNotBuilt();

        if (serviceType.IsGenericTypeDefinition ^ decoratorType.IsGenericTypeDefinition)
        {
            throw new RegistrationException(
                $"Cannot register decorator '{decoratorType.FullName}' for '{serviceType.FullName}': both must be open generic type definitions, or neither.");
        }
        if (!decoratorType.IsClass || decoratorType.IsAbstract)
        {
            throw new RegistrationException(
                $"Decorator '{decoratorType.FullName}' must be a concrete, non-abstract class.");
        }
        if (!DecoratorImplementsService(serviceType, decoratorType))
        {
            throw new RegistrationException(
                $"Decorator '{decoratorType.FullName}' does not implement service type '{serviceType.FullName}'.");
        }

        var reg = Registration.ForDecorator(serviceType, decoratorType, _orderCounter++);
        _registrations.Add(reg);
        return this;
    }

    // Closed types use plain assignability. For open generics the decorator's own type parameters
    // stand in for the service's, so compare generic type definitions across its bases / interfaces.
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    private static bool DecoratorImplementsService(Type serviceType, Type decoratorType)
    {
        if (!serviceType.IsGenericTypeDefinition) return serviceType.IsAssignableFrom(decoratorType);

        for (var t = decoratorType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == serviceType) return true;
        }
        foreach (var iface in decoratorType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == serviceType) return true;
        }
        return false;
    }

    /// <inheritdoc />
    public IContainerBuilder RegisterModule(IModule module)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));
        EnsureNotBuilt();
        module.Register(this);
        return this;
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public IContainerBuilder RegisterAssembly(Assembly asm, Predicate<Type>? filter = null, Lifetime lifetime = Lifetime.Transient)
    {
        if (asm is null) throw new ArgumentNullException(nameof(asm));
        EnsureNotBuilt();

        foreach (var type in asm.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition) continue;
            if (!type.IsPublic && !type.IsNestedPublic) continue;
            if (filter is not null && !filter(type)) continue;

            // Register against the type itself.
            var selfReg = Registration.ForType(type, type, lifetime, _orderCounter++);
            _registrations.Add(selfReg);

            // Register against every interface (except IDisposable / IAsyncDisposable).
            foreach (var iface in type.GetInterfaces())
            {
                if (iface == typeof(IDisposable)) continue;
                if (iface.FullName == "System.IAsyncDisposable") continue;
                var ifaceReg = Registration.ForType(iface, type, lifetime, _orderCounter++);
                _registrations.Add(ifaceReg);
            }
        }
        return this;
    }

    /// <inheritdoc />
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public IContainer Build() => Build(new BuildOptions());

    /// <inheritdoc />
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public IContainer Build(BuildOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        EnsureNotBuilt();
        ValidateKeys();
        ValidateDecorators();
        _built = true;
        var container = new Container(_registrations);
        if (options.ValidateOnBuild)
        {
            container.ValidateAllRegistrations();
        }
        if (options.ValidateScopes)
        {
            container.ValidateNoCaptiveDependencies();
        }
        return container;
    }

    private void EnsureNotBuilt()
    {
        if (_built) throw new InvalidOperationException("This ContainerBuilder has already been built. Create a new one if you need another container.");
    }

    private void ValidateKeys()
    {
        // Duplicate keys for the same service type are an error.
        var seen = new HashSet<(Type, object)>();
        foreach (var reg in _registrations)
        {
            if (reg.Key is null) continue;
            if (!seen.Add((reg.ServiceType, reg.Key)))
            {
                throw new RegistrationException(
                    $"Duplicate key '{reg.Key}' for service type '{reg.ServiceType.FullName}'.");
            }
        }
    }

    private void ValidateDecorators()
    {
        // Each decorator must have at least one matching non-decorator registration for its service type.
        // For open-generic service types, accept any matching open-generic OR a matching closed registration.
        var nonDecoratorTypes = new HashSet<Type>();
        // Generic definitions of every closed-generic base (IRepository<User> -> IRepository<>), so an
        // open-generic decorator is satisfied by closed-only bases too.
        var closedBaseDefinitions = new HashSet<Type>();
        foreach (var reg in _registrations)
        {
            if (reg.IsDecorator) continue;
            nonDecoratorTypes.Add(reg.ServiceType);
            if (reg.ServiceType.IsConstructedGenericType)
            {
                closedBaseDefinitions.Add(reg.ServiceType.GetGenericTypeDefinition());
            }
        }

        foreach (var reg in _registrations)
        {
            if (!reg.IsDecorator) continue;

            // Exact match: a closed decorator satisfied by a closed base, or an open-generic decorator
            // (e.g. on IRepository<>) satisfied by an open-generic base registered on the same definition.
            if (nonDecoratorTypes.Contains(reg.ServiceType)) continue;

            // A closed decorator (e.g. on IRepository<User>) is also satisfied by a matching
            // open-generic base (IRepository<>), since the container closes that base on demand.
            if (reg.ServiceType.IsConstructedGenericType
                && nonDecoratorTypes.Contains(reg.ServiceType.GetGenericTypeDefinition()))
            {
                continue;
            }

            // An open-generic decorator (on IRepository<>) is satisfied by any closed base of that
            // definition (IRepository<User>); it wraps exactly those closed resolutions at runtime.
            if (reg.ServiceType.IsGenericTypeDefinition && closedBaseDefinitions.Contains(reg.ServiceType))
            {
                continue;
            }

            throw new RegistrationException(
                $"Decorator for service type '{reg.ServiceType.FullName}' has no matching base registration.");
        }
    }

    private sealed class RegistrationBuilder : IRegistrationBuilder
    {
        private readonly ContainerBuilder _owner;
        private readonly Registration _reg;

        public RegistrationBuilder(ContainerBuilder owner, Registration reg)
        {
            _owner = owner;
            _reg = reg;
        }

        public IRegistrationBuilder AsTransient() { _reg.SetLifetime(Lifetime.Transient); return this; }
        public IRegistrationBuilder AsScoped() { _reg.SetLifetime(Lifetime.Scoped); return this; }
        public IRegistrationBuilder AsSingleton() { _reg.SetLifetime(Lifetime.Singleton); return this; }

        public IRegistrationBuilder WithKey(object key)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            _reg.SetKey(key);
            return this;
        }

        [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
            Justification = "The IResolver.Resolve call is captured in a factory delegate that only runs at resolution time, which is itself gated by the [RequiresUnreferencedCode]-annotated public Resolve/Build entry points.")]
        [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
            Justification = "The IResolver.Resolve call is captured in a factory delegate that only runs at resolution time, which is itself gated by the [RequiresDynamicCode]-annotated public Resolve/Build entry points.")]
        public IRegistrationBuilder As(params Type[] additionalServiceTypes)
        {
            if (additionalServiceTypes is null) throw new ArgumentNullException(nameof(additionalServiceTypes));
            var primary = _reg;
            var primaryService = _reg.ServiceType;
            foreach (var t in additionalServiceTypes)
            {
                if (t is null) throw new ArgumentException("Service type cannot be null.", nameof(additionalServiceTypes));
                if (!_reg.Aliases.Contains(t)) _reg.Aliases.Add(t);

                // Alias resolves *through* the primary registration so they share its lifetime cache.
                // Lifetime on the alias itself must be Transient — the singleton/scoped semantics live
                // on the underlying registration that the factory delegates to. A keyed primary is
                // only reachable by key, so the key is read at resolve time (WithKey may follow As).
                var alias = Registration.ForFactory(
                    t,
                    r => primary.Key is null ? r.Resolve(primaryService) : r.ResolveKeyed(primaryService, primary.Key),
                    Lifetime.Transient,
                    _owner._orderCounter++);
                if (_reg.Key is not null) alias.SetKey(_reg.Key);
                _owner._registrations.Add(alias);
            }
            return this;
        }

        public IRegistrationBuilder OnActivating(Action<object> callback)
        {
            if (callback is null) throw new ArgumentNullException(nameof(callback));
            _reg.OnActivatingHooks.Add(callback);
            return this;
        }

        public IRegistrationBuilder OnActivated(Action<object> callback)
        {
            if (callback is null) throw new ArgumentNullException(nameof(callback));
            _reg.OnActivatedHooks.Add(callback);
            return this;
        }
    }
}
