using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace TechTeaStudio.Injector;

/// <summary>
/// Built dependency injection container. Caches one compiled activator per
/// <c>(registration, lifetime)</c> tuple and serves as the root <see cref="IResolver"/>.
/// </summary>
public sealed class Container : IContainer
{
    private readonly List<Registration> _registrations;
    private readonly Dictionary<Type, List<Registration>> _byServiceType;
    private readonly Dictionary<Registration, Func<IResolver, object>> _activatorCache = new();
    private readonly Dictionary<Registration, object> _singletons = new();
    private readonly List<IDisposable> _ownedDisposables = new();
    private readonly object _lock = new();
    private bool _disposed;

    // Ambient cycle-detection chain. Lives on the resolving thread / async-flow.
    [ThreadStatic]
    private static Stack<Type>? _resolvingChain;
    [ThreadStatic]
    private static HashSet<Type>? _resolvingSet;

    internal Container(IReadOnlyList<Registration> registrations)
    {
        _registrations = registrations.ToList();
        _byServiceType = new Dictionary<Type, List<Registration>>();

        foreach (var reg in _registrations)
        {
            if (reg.IsDecorator) continue; // decorators are looked up separately

            if (!_byServiceType.TryGetValue(reg.ServiceType, out var list))
            {
                list = new List<Registration>();
                _byServiceType[reg.ServiceType] = list;
            }
            list.Add(reg);
        }

        // Pre-cache singleton instances (RegisterInstance pre-fills `Instance`).
        foreach (var reg in _registrations)
        {
            if (reg.Lifetime == Lifetime.Singleton && reg.Instance is not null)
            {
                _singletons[reg] = reg.Instance;
                if (reg.Instance is IDisposable d) _ownedDisposables.Add(d);
            }
        }
    }

    /// <inheritdoc />
    public IScope CreateScope()
    {
        EnsureNotDisposed();
        return new Scope(this);
    }

    /// <inheritdoc />
    public T Resolve<T>() => (T)Resolve(typeof(T));

    /// <inheritdoc />
    public object Resolve(Type serviceType) => ResolveInternal(serviceType, scope: null);

    /// <inheritdoc />
    public IEnumerable<T> ResolveAll<T>() => ResolveAll(typeof(T)).Cast<T>();

    /// <inheritdoc />
    public IEnumerable<object> ResolveAll(Type serviceType) => ResolveAllInternal(serviceType, scope: null);

    /// <inheritdoc />
    public IDictionary<TKey, T> ResolveDictionary<TKey, T>() where TKey : notnull
        => ResolveDictionaryInternal<TKey, T>(scope: null);

    internal object ResolveInternal(Type serviceType, Scope? scope)
    {
        EnsureNotDisposed();
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));

        // Built-in collection support.
        if (TryResolveCollection(serviceType, scope, out var collection)) return collection!;

        var registrations = GetRegistrations(serviceType);
        if (registrations.Count == 0)
        {
            throw new ServiceNotRegisteredException(serviceType);
        }

        // Last-registered wins for a single resolve (Microsoft.Extensions.DependencyInjection convention).
        var reg = registrations[^1];
        return Activate(reg, scope, new HashSet<Type>(), new Stack<Type>());
    }

    internal IEnumerable<object> ResolveAllInternal(Type serviceType, Scope? scope)
    {
        EnsureNotDisposed();
        var regs = GetRegistrations(serviceType);
        var results = new List<object>(regs.Count);
        foreach (var reg in regs)
        {
            results.Add(Activate(reg, scope, new HashSet<Type>(), new Stack<Type>()));
        }
        return results;
    }

    internal IDictionary<TKey, T> ResolveDictionaryInternal<TKey, T>(Scope? scope) where TKey : notnull
    {
        EnsureNotDisposed();
        var dict = new Dictionary<TKey, T>();
        var regs = GetRegistrations(typeof(T));
        foreach (var reg in regs)
        {
            if (reg.Key is null) continue;
            if (reg.Key is not TKey typedKey)
            {
                throw new InjectorException(
                    $"Registration for '{typeof(T).FullName}' has key of type '{reg.Key.GetType().FullName}', " +
                    $"but ResolveDictionary was called with key type '{typeof(TKey).FullName}'.");
            }
            dict[typedKey] = (T)Activate(reg, scope, new HashSet<Type>(), new Stack<Type>());
        }
        return dict;
    }

    private bool TryResolveCollection(Type serviceType, Scope? scope, out object? value)
    {
        value = null;
        if (!serviceType.IsGenericType) return false;
        var def = serviceType.GetGenericTypeDefinition();
        var args = serviceType.GetGenericArguments();

        if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IReadOnlyList<>))
        {
            var elem = args[0];
            var items = ResolveAllInternal(elem, scope);
            var arr = Array.CreateInstance(elem, items.Count());
            var i = 0;
            foreach (var it in items) arr.SetValue(it, i++);
            value = arr;
            return true;
        }

        if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
        {
            var keyType = args[0];
            var elemType = args[1];
            var method = typeof(Container)
                .GetMethod(nameof(BuildDictionary), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(keyType, elemType);
            value = method.Invoke(this, new object?[] { scope });
            return true;
        }

        return false;
    }

    private IDictionary<TKey, TElem> BuildDictionary<TKey, TElem>(Scope? scope) where TKey : notnull
        => ResolveDictionaryInternal<TKey, TElem>(scope);

    private List<Registration> GetRegistrations(Type serviceType)
    {
        return _byServiceType.TryGetValue(serviceType, out var list) ? list : new List<Registration>();
    }

    private List<Registration> GetDecorators(Type serviceType)
    {
        var list = new List<Registration>();
        foreach (var reg in _registrations)
        {
            if (reg.IsDecorator && reg.ServiceType == serviceType) list.Add(reg);
        }
        return list;
    }

    internal object Activate(Registration reg, Scope? scope, HashSet<Type> visiting, Stack<Type> chain)
    {
        // Lifetime caching.
        switch (reg.Lifetime)
        {
            case Lifetime.Singleton:
                lock (_lock)
                {
                    if (_singletons.TryGetValue(reg, out var existing)) return existing;
                }
                var singleton = CreateInstance(reg, scope, visiting, chain);
                lock (_lock)
                {
                    if (!_singletons.TryGetValue(reg, out var raceWinner))
                    {
                        _singletons[reg] = singleton;
                        if (singleton is IDisposable d) _ownedDisposables.Add(d);
                        return singleton;
                    }
                    // Lost the race — drop our duplicate (potentially disposing it).
                    if (!ReferenceEquals(raceWinner, singleton) && singleton is IDisposable disp)
                    {
                        try { disp.Dispose(); } catch { /* swallow */ }
                    }
                    return raceWinner;
                }

            case Lifetime.Scoped:
                if (scope is null)
                {
                    throw new InjectorException(
                        $"Service '{reg.ServiceType.FullName}' is scoped; it can only be resolved from an IScope (see container.CreateScope()).");
                }
                return scope.GetOrCreateScoped(reg, () => CreateInstance(reg, scope, visiting, chain));

            case Lifetime.Transient:
            default:
                var t = CreateInstance(reg, scope, visiting, chain);
                if (scope is not null && t is IDisposable td) scope.TrackTransient(td);
                else if (scope is null && t is IDisposable rd) lock (_lock) { _ownedDisposables.Add(rd); }
                return t;
        }
    }

    private object CreateInstance(Registration reg, Scope? scope, HashSet<Type> visiting, Stack<Type> chain)
    {
        // Pre-built instance.
        if (reg.Instance is not null) return reg.Instance;

        // Factory delegate.
        if (reg.Factory is not null)
        {
            IResolver effective = (IResolver?)scope ?? this;
            var produced = reg.Factory(effective);
            return ApplyDecorators(reg.ServiceType, produced, scope, visiting, chain);
        }

        // Type-based.
        var implType = reg.ImplementationType
            ?? throw new InjectorException($"Registration for '{reg.ServiceType.FullName}' has no implementation type.");

        // Use ambient (thread-static) cycle detection state so cycles that span the
        // compiled-delegate boundary are still caught at runtime.
        var ambientSet = _resolvingSet ??= new HashSet<Type>();
        var ambientChain = _resolvingChain ??= new Stack<Type>();

        ambientChain.Push(reg.ServiceType);
        if (!ambientSet.Add(reg.ServiceType))
        {
            var snapshot = ambientChain.Reverse().ToArray();
            // Pop before throwing so the next resolve starts clean.
            ambientChain.Pop();
            throw new CircularDependencyException(snapshot);
        }
        try
        {
            var activator = GetOrCompileActivator(reg, implType, visiting, chain);
            IResolver effective = (IResolver?)scope ?? this;
            var raw = activator(effective);
            return ApplyDecorators(reg.ServiceType, raw, scope, visiting, chain);
        }
        finally
        {
            ambientSet.Remove(reg.ServiceType);
            if (ambientChain.Count > 0) ambientChain.Pop();
            // Reset the ambient state once we unwind to the top level.
            if (ambientChain.Count == 0)
            {
                _resolvingChain = null;
                _resolvingSet = null;
            }
        }
    }

    private Func<IResolver, object> GetOrCompileActivator(Registration reg, Type implType, HashSet<Type> visiting, Stack<Type> chain)
    {
        lock (_lock)
        {
            if (_activatorCache.TryGetValue(reg, out var cached)) return cached;
        }

        var resolverParam = Expression.Parameter(typeof(IResolver), "r");
        Expression ResolveDep(Type depType)
        {
            // Use IResolver.Resolve(Type) at runtime so dependent registrations see the right scope.
            // Cycle detection happens at runtime via the ambient stack in CreateInstance.
            var resolveMethod = typeof(IResolver).GetMethod(nameof(IResolver.Resolve), new[] { typeof(Type) })!;
            return Expression.Call(resolverParam, resolveMethod, Expression.Constant(depType, typeof(Type)));
        }

        bool IsResolvable(Type t)
        {
            if (t == typeof(IResolver) || t == typeof(IContainer) || t == typeof(IScope)) return true;
            if (_byServiceType.ContainsKey(t)) return true;
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IReadOnlyList<>)
                    || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                {
                    return true;
                }
            }
            return false;
        }

        var body = Activators.BuildActivatorExpression(implType, resolverParam, ResolveDep, IsResolvable);
        var lambda = Expression.Lambda<Func<IResolver, object>>(body, resolverParam);
        var compiled = lambda.Compile();

        lock (_lock)
        {
            _activatorCache[reg] = compiled;
        }
        return compiled;
    }

    private object ApplyDecorators(Type serviceType, object inner, Scope? scope, HashSet<Type> visiting, Stack<Type> chain)
    {
        var decorators = GetDecorators(serviceType);
        if (decorators.Count == 0) return inner;

        object current = inner;
        foreach (var dec in decorators)
        {
            var decType = dec.DecoratorType!;
            // Build an activator that constructs the decorator with `current` for the service-type parameter
            // and resolves other ctor parameters normally.
            var ctor = Activators.SelectConstructor(decType, _ => true);
            var args = new List<object?>();
            foreach (var p in ctor.GetParameters())
            {
                if (p.ParameterType == serviceType || p.ParameterType.IsInstanceOfType(current))
                {
                    args.Add(current);
                }
                else
                {
                    args.Add(ResolveInternalSafe(p.ParameterType, scope, visiting, chain));
                }
            }
            var built = ctor.Invoke(args.ToArray());

            // Apply property + method injection on the decorator too.
            ApplyPropertyMethodInjection(built, scope);

            current = built;
        }
        return current;
    }

    private object ResolveInternalSafe(Type type, Scope? scope, HashSet<Type> visiting, Stack<Type> chain)
    {
        // Same as ResolveInternal but threads the existing visiting set so decorators participate in cycle detection.
        if (TryResolveCollection(type, scope, out var coll)) return coll!;

        var regs = GetRegistrations(type);
        if (regs.Count == 0) throw new ServiceNotRegisteredException(type);
        return Activate(regs[^1], scope, visiting, chain);
    }

    private void ApplyPropertyMethodInjection(object obj, Scope? scope)
    {
        var t = obj.GetType();
        IResolver effective = (IResolver?)scope ?? this;
        foreach (var prop in Activators.GetInjectableProperties(t))
        {
            prop.SetValue(obj, effective.Resolve(prop.PropertyType));
        }
        foreach (var method in Activators.GetInjectableMethods(t))
        {
            var args = method.GetParameters().Select(p => effective.Resolve(p.ParameterType)).ToArray();
            method.Invoke(obj, args);
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Container));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose owned singletons / root-tracked transients in reverse creation order.
        lock (_lock)
        {
            for (var i = _ownedDisposables.Count - 1; i >= 0; i--)
            {
                try { _ownedDisposables[i].Dispose(); } catch { /* swallow */ }
            }
            _ownedDisposables.Clear();
            _singletons.Clear();
            _activatorCache.Clear();
        }
    }
}
