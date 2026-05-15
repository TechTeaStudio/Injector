using System.Reflection;

namespace TechTeaStudio.Injector;

/// <summary>
/// Default in-memory implementation of <see cref="IContainerBuilder"/>. Collects registrations,
/// then hands them off to the resolution engine when <see cref="Build"/> is called.
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
    public IContainerBuilder RegisterModule(IModule module)
    {
        if (module is null) throw new ArgumentNullException(nameof(module));
        EnsureNotBuilt();
        module.Register(this);
        return this;
    }

    /// <inheritdoc />
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
    public IContainer Build()
    {
        EnsureNotBuilt();
        ValidateKeys();
        _built = true;
        return new Container(_registrations);
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

        public IRegistrationBuilder As(params Type[] additionalServiceTypes)
        {
            if (additionalServiceTypes is null) throw new ArgumentNullException(nameof(additionalServiceTypes));
            var primaryService = _reg.ServiceType;
            foreach (var t in additionalServiceTypes)
            {
                if (t is null) throw new ArgumentException("Service type cannot be null.", nameof(additionalServiceTypes));
                if (!_reg.Aliases.Contains(t)) _reg.Aliases.Add(t);

                // Alias resolves *through* the primary registration so they share its lifetime cache.
                // Lifetime on the alias itself must be Transient — the singleton/scoped semantics live
                // on the underlying registration that the factory delegates to.
                var alias = Registration.ForFactory(t, r => r.Resolve(primaryService), Lifetime.Transient, _owner._orderCounter++);
                if (_reg.Key is not null) alias.SetKey(_reg.Key);
                _owner._registrations.Add(alias);
            }
            return this;
        }
    }
}
