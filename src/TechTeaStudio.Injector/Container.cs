using System.Collections;
using System.Collections.Concurrent;
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
    // Closed-type lookup. A ConcurrentDictionary so lock-free readers on hot paths are safe while
    // GetRegistrations lazily publishes a synthesized closed-generic list (see FIX 1). Writes are
    // still serialized under _lock so an open generic is synthesized at most once.
    private readonly ConcurrentDictionary<Type, List<Registration>> _byServiceType;
    // Open-generic definitions. Populated only in the constructor and never mutated afterwards, so
    // a plain Dictionary is safe for concurrent reads.
    private readonly Dictionary<Type, List<Registration>> _openGenericsByDef;
    private readonly Dictionary<Registration, Func<IResolver, object>> _activatorCache = new();
    private readonly Dictionary<Registration, object> _singletons = new();
    // Per-registration single-construction guard for singletons (see FIX 2). Each Lazy<object> uses
    // ExecutionAndPublication so concurrent resolvers share exactly one construction, and `.Value`
    // is evaluated OUTSIDE _lock so a singleton ctor may safely re-enter the container.
    private readonly Dictionary<Registration, Lazy<object>> _singletonFactories = new();
    /// <summary>
    /// Owned instances kept for disposal at container teardown. The list preserves creation order
    /// (reversed at dispose time) and may contain entries that are <see cref="IDisposable"/>,
    /// <see cref="IAsyncDisposable"/>, or both — see <see cref="DisposalHelpers"/> for the policy.
    /// </summary>
    private readonly List<object> _ownedDisposables = new();
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
        _byServiceType = new ConcurrentDictionary<Type, List<Registration>>();
        _openGenericsByDef = new Dictionary<Type, List<Registration>>();

        foreach (var reg in _registrations)
        {
            if (reg.IsDecorator) continue; // decorators are looked up separately

            if (reg.IsOpenGeneric)
            {
                if (!_openGenericsByDef.TryGetValue(reg.ServiceType, out var openList))
                {
                    openList = new List<Registration>();
                    _openGenericsByDef[reg.ServiceType] = openList;
                }
                openList.Add(reg);
                continue;
            }

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
                if (DisposalHelpers.IsTrackable(reg.Instance)) _ownedDisposables.Add(reg.Instance);
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

        // Child-scope-first lookup: walk the scope chain and pick the closest registration.
        if (scope is not null && TryResolveFromScopeChain(serviceType, scope, out var fromChild)) return fromChild!;

        var registrations = GetRegistrations(serviceType);
        if (registrations.Count == 0)
        {
            // Synthetic Lazy<T> / Func<T> / Func<P,T> resolution: only kicks in when the
            // exact type is unregistered. A user-supplied registration always wins.
            if (TryResolveLazyOrFunc(serviceType, scope, out var synthesized)) return synthesized!;

            // Built-in resolver / scope / container parameter support, for cases where a
            // ctor wants the active resolver itself.
            if (serviceType == typeof(IResolver) || serviceType == typeof(IScope))
            {
                return (object?)scope ?? this;
            }
            if (serviceType == typeof(IContainer)) return this;

            throw new ServiceNotRegisteredException(serviceType);
        }

        // Last-registered wins for a single resolve (Microsoft.Extensions.DependencyInjection convention).
        var reg = registrations[^1];
        return Activate(reg, scope, new HashSet<Type>(), new Stack<Type>());
    }

    private bool TryResolveFromScopeChain(Type serviceType, Scope scope, out object? value)
    {
        // Walk from the inner-most scope up. The first scope with a matching extra registration wins.
        Scope? current = scope;
        while (current is not null)
        {
            var extra = current.Extra;
            if (extra is not null)
            {
                if (extra.TryGet(serviceType, out var regs) && regs is not null && regs.Count > 0)
                {
                    value = Activate(regs[^1], current, new HashSet<Type>(), new Stack<Type>());
                    return true;
                }
                if (serviceType.IsConstructedGenericType
                    && extra.TryGetOpenGeneric(serviceType.GetGenericTypeDefinition(), out var openList)
                    && openList is not null && openList.Count > 0)
                {
                    // Materialize a closed registration on the fly, similar to the container-level path.
                    var openReg = openList[^1];
                    var openImpl = openReg.ImplementationType!;
                    var closedImpl = openImpl.MakeGenericType(serviceType.GenericTypeArguments);
                    var closed = Registration.ForType(serviceType, closedImpl, openReg.Lifetime, openReg.Order);
                    if (openReg.Key is not null) closed.SetKey(openReg.Key);
                    closed.CopyLifecycleHooksFrom(openReg);
                    value = Activate(closed, current, new HashSet<Type>(), new Stack<Type>());
                    return true;
                }
            }
            current = current.Parent;
        }
        value = null;
        return false;
    }

    private bool TryResolveLazyOrFunc(Type serviceType, Scope? scope, out object? value)
    {
        value = null;
        if (!serviceType.IsGenericType) return false;
        var def = serviceType.GetGenericTypeDefinition();
        var args = serviceType.GetGenericArguments();

        if (def == typeof(Owned<>))
        {
            var inner = args[0];
            // Create a fresh scope (linked to the current scope as parent so parent registrations stay
            // visible) and resolve `inner` from it. The scope itself becomes the disposal context.
            Scope ownedScope;
            if (scope is not null)
            {
                ownedScope = new Scope(this, scope, ChildScopeRegistry.From(Array.Empty<Registration>()));
            }
            else
            {
                ownedScope = new Scope(this);
            }
            object resolvedInner;
            try
            {
                resolvedInner = ownedScope.Resolve(inner);
            }
            catch
            {
                ownedScope.Dispose();
                throw;
            }
            var ownedType = typeof(Owned<>).MakeGenericType(inner);
            value = Activator.CreateInstance(ownedType, resolvedInner, ownedScope)!;
            return true;
        }

        if (def == typeof(Lazy<>))
        {
            var inner = args[0];
            // new Lazy<T>(() => (T)resolver.Resolve(typeof(T)))
            var resolver = (IResolver?)scope ?? this;
            var funcType = typeof(Func<>).MakeGenericType(inner);
            var funcImpl = BuildResolveFunc(inner, resolver);
            var lazyCtor = serviceType.GetConstructor(new[] { funcType })
                ?? throw new InjectorException($"Lazy<{inner.FullName}> has no constructor taking Func<{inner.FullName}>.");
            value = lazyCtor.Invoke(new[] { funcImpl });
            return true;
        }

        if (def == typeof(Func<>))
        {
            var ret = args[0];
            var resolver = (IResolver?)scope ?? this;
            value = BuildResolveFunc(ret, resolver);
            return true;
        }

        // Func<P, T> ... Func<P1, P2, ..., T> — last arg is the return type, the rest are ctor parameters.
        if (def.FullName is { } name && name.StartsWith("System.Func`", StringComparison.Ordinal) && args.Length >= 2)
        {
            var ret = args[^1];
            var paramTypes = new Type[args.Length - 1];
            Array.Copy(args, 0, paramTypes, 0, paramTypes.Length);
            value = BuildFuncWithParams(serviceType, ret, paramTypes, scope);
            return true;
        }

        return false;
    }

    // Returns a Func<T> instance that resolves T from the supplied resolver each call.
    private static object BuildResolveFunc(Type returnType, IResolver resolver)
    {
        var method = typeof(Container)
            .GetMethod(nameof(MakeResolveFunc), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(returnType);
        return method.Invoke(null, new object[] { resolver })!;
    }

    private static Func<T> MakeResolveFunc<T>(IResolver resolver) => () => (T)resolver.Resolve(typeof(T));

    // Returns a Func<P1, ..., Pn, T> that constructs T using the supplied params + container-resolved deps.
    private object BuildFuncWithParams(Type funcType, Type returnType, Type[] paramTypes, Scope? scope)
    {
        // We need to build: (p1, p2, ..., pn) => CreateWithArgs(returnType, [p1...pn])
        // Then wrap that into the strongly-typed Func.
        var paramExprs = paramTypes.Select((t, i) => Expression.Parameter(t, "p" + i)).ToArray();
        // Capture `this` and `scope` and `paramTypes` in a closure:
        var capturedContainer = Expression.Constant(this, typeof(Container));
        var capturedScope = Expression.Constant(scope, typeof(Scope));
        var capturedReturn = Expression.Constant(returnType, typeof(Type));
        var capturedParamTypes = Expression.Constant(paramTypes, typeof(Type[]));
        var argsArray = Expression.NewArrayInit(typeof(object),
            paramExprs.Select(p => Expression.Convert(p, typeof(object))).ToArray());

        var call = Expression.Call(
            capturedContainer,
            typeof(Container).GetMethod(nameof(CreateWithExplicitArgs), BindingFlags.NonPublic | BindingFlags.Instance)!,
            capturedReturn, capturedParamTypes, argsArray, capturedScope);

        var converted = Expression.Convert(call, returnType);
        var lambda = Expression.Lambda(funcType, converted, paramExprs);
        return lambda.Compile();
    }

    internal object CreateWithExplicitArgs(Type targetType, Type[] paramTypes, object?[] paramValues, Scope? scope)
    {
        EnsureNotDisposed();
        // If targetType is an interface or has registrations, prefer the registered implementation.
        Type implType = targetType;
        if (_byServiceType.TryGetValue(targetType, out var regs) && regs.Count > 0)
        {
            var first = regs[^1];
            if (first.ImplementationType is not null) implType = first.ImplementationType;
        }
        // Pick the greediest ctor where the leading params match paramTypes and the rest are resolvable.
        var ctors = implType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .ToArray();

        bool IsResolvable(Type t)
        {
            if (t == typeof(IResolver) || t == typeof(IContainer) || t == typeof(IScope)) return true;
            if (_byServiceType.ContainsKey(t)) return true;
            if (t.IsConstructedGenericType && _openGenericsByDef.ContainsKey(t.GetGenericTypeDefinition())) return true;
            if (t.IsGenericType)
            {
                var d = t.GetGenericTypeDefinition();
                if (d == typeof(IEnumerable<>) || d == typeof(IReadOnlyCollection<>) || d == typeof(IReadOnlyList<>)
                    || d == typeof(IDictionary<,>) || d == typeof(IReadOnlyDictionary<,>)
                    || d == typeof(Lazy<>)
                    || d == typeof(Owned<>)
                    || (d.FullName is { } n && n.StartsWith("System.Func`", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            return false;
        }

        IResolver effective = (IResolver?)scope ?? this;
        foreach (var ctor in ctors)
        {
            var ps = ctor.GetParameters();
            // Greedy match: a ctor whose first parameters consume the supplied args, in order,
            // and whose remaining parameters are resolvable.
            if (ps.Length < paramTypes.Length) continue;

            // Check the explicit-args slots up front.
            var used = new bool[paramTypes.Length];
            var assignment = new int[ps.Length];
            for (var i = 0; i < ps.Length; i++) assignment[i] = -1;

            // Greedy first-match: scan params in order; for each, if an unused supplied arg of
            // assignable type exists, pair them. Otherwise leave the slot for the container.
            for (var i = 0; i < ps.Length; i++)
            {
                for (var k = 0; k < paramTypes.Length; k++)
                {
                    if (used[k]) continue;
                    if (paramTypes[k].IsAssignableTo(ps[i].ParameterType)
                        || ps[i].ParameterType.IsAssignableFrom(paramTypes[k]))
                    {
                        assignment[i] = k;
                        used[k] = true;
                        break;
                    }
                }
            }

            // Did we consume every supplied arg?
            if (used.Any(u => !u)) continue;

            // Are the non-assigned ctor params resolvable?
            var unresolvable = false;
            for (var i = 0; i < ps.Length; i++)
            {
                if (assignment[i] >= 0) continue;
                if (!IsResolvable(ps[i].ParameterType)) { unresolvable = true; break; }
            }
            if (unresolvable) continue;

            // Build the argument array.
            var actual = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                if (assignment[i] >= 0) actual[i] = paramValues[assignment[i]];
                else actual[i] = effective.Resolve(ps[i].ParameterType);
            }
            var built = ctor.Invoke(actual);

            // Apply property + method injection.
            ApplyPropertyMethodInjection(built, scope);
            return built;
        }

        throw new InjectorException(
            $"No public constructor on '{implType.FullName}' can accept the supplied Func<...> parameters [{string.Join(", ", paramTypes.Select(t => t.FullName))}] and resolve the rest from the container.");
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
        // Include child-scope extras (outermost child first to mirror registration semantics).
        if (scope is not null)
        {
            // Build path from root child to inner-most child so registration order is preserved.
            var chain = new List<Scope>();
            Scope? current = scope;
            while (current is not null) { chain.Add(current); current = current.Parent; }
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var extra = chain[i].Extra;
                if (extra is null) continue;
                if (extra.TryGet(serviceType, out var extraRegs) && extraRegs is not null)
                {
                    foreach (var reg in extraRegs)
                    {
                        results.Add(Activate(reg, chain[i], new HashSet<Type>(), new Stack<Type>()));
                    }
                }
            }
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
        // Lock-free fast path. Safe because _byServiceType is a ConcurrentDictionary, so this read
        // can race with the publishing write below (and with other hot-path readers) without tearing.
        if (_byServiceType.TryGetValue(serviceType, out var list)) return list;

        // No closed registration; try open-generic fallback.
        if (serviceType.IsConstructedGenericType)
        {
            var def = serviceType.GetGenericTypeDefinition();
            if (_openGenericsByDef.TryGetValue(def, out var openList))
            {
                // Serialize synthesis under _lock so MakeGenericType runs at most once per closed
                // type; the synthesized list is then published into the concurrent dictionary.
                lock (_lock)
                {
                    // Double-check after taking the lock — another thread may have synthesized.
                    if (_byServiceType.TryGetValue(serviceType, out var closedList)) return closedList;

                    var synthesized = new List<Registration>(openList.Count);
                    foreach (var openReg in openList)
                    {
                        var openImpl = openReg.ImplementationType
                            ?? throw new InjectorException($"Open-generic registration for '{openReg.ServiceType.FullName}' has no implementation type.");
                        Type closedImpl;
                        try
                        {
                            closedImpl = openImpl.MakeGenericType(serviceType.GenericTypeArguments);
                        }
                        catch (ArgumentException ex)
                        {
                            throw new InjectorException(
                                $"Cannot close open-generic implementation '{openImpl.FullName}' for service '{serviceType.FullName}': {ex.Message}",
                                ex);
                        }

                        var closed = Registration.ForType(serviceType, closedImpl, openReg.Lifetime, openReg.Order);
                        if (openReg.Key is not null) closed.SetKey(openReg.Key);
                        // Copy over lifecycle hooks if any.
                        closed.CopyLifecycleHooksFrom(openReg);
                        synthesized.Add(closed);
                    }

                    _byServiceType[serviceType] = synthesized;
                    return synthesized;
                }
            }
        }

        return new List<Registration>();
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
            {
                // Fast path: already-materialized singleton (includes RegisterInstance singletons
                // pre-filled in the constructor).
                lock (_lock)
                {
                    if (_singletons.TryGetValue(reg, out var existing)) return existing;
                }

                // Get-or-create a per-registration Lazy<object> so concurrent resolvers share a
                // single construction. Only the cheap factory bookkeeping happens under _lock; the
                // actual user constructor runs when .Value is read OUTSIDE the lock, so a singleton
                // ctor that resolves another service can safely re-enter the container.
                Lazy<object> lazy;
                lock (_lock)
                {
                    if (!_singletonFactories.TryGetValue(reg, out lazy!))
                    {
                        lazy = new Lazy<object>(
                            () => CreateInstance(reg, scope, visiting, chain),
                            LazyThreadSafetyMode.ExecutionAndPublication);
                        _singletonFactories[reg] = lazy;
                    }
                }

                // Runs the constructor at most once across all threads (and at most once total).
                var singleton = lazy.Value;

                // Publish + register for disposal exactly once. Re-entrancy (a singleton ctor that
                // resolves the same singleton) lands here twice for one Lazy; the membership check
                // keeps _ownedDisposables free of duplicates.
                lock (_lock)
                {
                    if (!_singletons.ContainsKey(reg))
                    {
                        _singletons[reg] = singleton;
                        if (DisposalHelpers.IsTrackable(singleton)) _ownedDisposables.Add(singleton);
                    }
                }
                return singleton;
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
                if (scope is not null && DisposalHelpers.IsTrackable(t)) scope.TrackTransient(t);
                else if (scope is null && DisposalHelpers.IsTrackable(t)) lock (_lock) { _ownedDisposables.Add(t); }
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
            IResolver effective = (IResolver?)scope ?? this;
            object instance;

            if (reg.OnActivatingHooks.Count > 0 || reg.OnActivatedHooks.Count > 0)
            {
                // Slow path: construction first, then OnActivating, then property/method injection, then OnActivated.
                var ctorActivator = GetOrCompileConstructorOnlyActivator(reg, implType);
                instance = ctorActivator(effective);
                foreach (var hook in reg.OnActivatingHooks) hook(instance);
                ApplyPropertyMethodInjection(instance, scope);
                foreach (var hook in reg.OnActivatedHooks) hook(instance);
            }
            else
            {
                var activator = GetOrCompileActivator(reg, implType, visiting, chain);
                instance = activator(effective);
            }

            return ApplyDecorators(reg.ServiceType, instance, scope, visiting, chain);
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

    private readonly Dictionary<Registration, Func<IResolver, object>> _ctorOnlyActivatorCache = new();

    private Func<IResolver, object> GetOrCompileConstructorOnlyActivator(Registration reg, Type implType)
    {
        lock (_lock)
        {
            if (_ctorOnlyActivatorCache.TryGetValue(reg, out var cached)) return cached;
        }
        var resolverParam = Expression.Parameter(typeof(IResolver), "r");
        Expression ResolveDep(Type depType)
        {
            var resolveMethod = typeof(IResolver).GetMethod(nameof(IResolver.Resolve), new[] { typeof(Type) })!;
            return Expression.Call(resolverParam, resolveMethod, Expression.Constant(depType, typeof(Type)));
        }
        var body = Activators.BuildConstructorOnlyExpression(implType, resolverParam, ResolveDep, BuiltInIsResolvable);
        var lambda = Expression.Lambda<Func<IResolver, object>>(body, resolverParam);
        var compiled = lambda.Compile();
        lock (_lock)
        {
            _ctorOnlyActivatorCache[reg] = compiled;
        }
        return compiled;
    }

    private bool BuiltInIsResolvable(Type t)
    {
        if (t == typeof(IResolver) || t == typeof(IContainer) || t == typeof(IScope)) return true;
        if (_byServiceType.ContainsKey(t)) return true;
        if (t.IsConstructedGenericType && _openGenericsByDef.ContainsKey(t.GetGenericTypeDefinition())) return true;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                return true;
            }
            if (def == typeof(Lazy<>)) return true;
            if (def == typeof(Owned<>)) return true;
            if (def.FullName is { } name && name.StartsWith("System.Func`", StringComparison.Ordinal))
            {
                var args = t.GetGenericArguments();
                if (args.Length == 1) return BuiltInIsResolvable(args[0]);
                return true;
            }
        }
        return false;
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
            if (t.IsConstructedGenericType && _openGenericsByDef.ContainsKey(t.GetGenericTypeDefinition())) return true;
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IReadOnlyList<>)
                    || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                {
                    return true;
                }
                if (def == typeof(Lazy<>)) return true;
                if (def == typeof(Owned<>)) return true;
                if (def.FullName is { } name && name.StartsWith("System.Func`", StringComparison.Ordinal))
                {
                    // Func<T> requires T be resolvable; Func<P, T> is satisfied because we synthesize an activator.
                    var args = t.GetGenericArguments();
                    if (args.Length == 1) return IsResolvable(args[0]);
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

    private readonly Dictionary<Registration, Func<IResolver, object, object>> _decoratorActivatorCache = new();

    private object ApplyDecorators(Type serviceType, object inner, Scope? scope, HashSet<Type> visiting, Stack<Type> chain)
    {
        var decorators = GetDecorators(serviceType);
        if (decorators.Count == 0) return inner;

        IResolver effective = (IResolver?)scope ?? this;
        object current = inner;
        foreach (var dec in decorators)
        {
            var activator = GetOrCompileDecoratorActivator(dec, serviceType);
            var built = activator(effective, current);

            // Apply property + method injection on the decorator too.
            ApplyPropertyMethodInjection(built, scope);

            current = built;
        }
        return current;
    }

    private Func<IResolver, object, object> GetOrCompileDecoratorActivator(Registration dec, Type serviceType)
    {
        lock (_lock)
        {
            if (_decoratorActivatorCache.TryGetValue(dec, out var cached)) return cached;
        }

        var decType = dec.DecoratorType
            ?? throw new InjectorException($"Decorator registration for '{dec.ServiceType.FullName}' has no decorator type.");
        var ctor = Activators.SelectConstructor(decType, _ => true);

        var resolverParam = Expression.Parameter(typeof(IResolver), "r");
        var innerParam = Expression.Parameter(typeof(object), "inner");

        var ctorArgExprs = new List<Expression>();
        var consumedInner = false;
        foreach (var p in ctor.GetParameters())
        {
            if (!consumedInner && p.ParameterType.IsAssignableFrom(serviceType))
            {
                // bind `current` for the first matching parameter.
                ctorArgExprs.Add(Expression.Convert(innerParam, p.ParameterType));
                consumedInner = true;
            }
            else
            {
                // resolver.Resolve(p.ParameterType) cast to p.ParameterType
                var resolveMethod = typeof(IResolver).GetMethod(nameof(IResolver.Resolve), new[] { typeof(Type) })!;
                var call = Expression.Call(resolverParam, resolveMethod, Expression.Constant(p.ParameterType, typeof(Type)));
                ctorArgExprs.Add(Expression.Convert(call, p.ParameterType));
            }
        }

        var newExpr = Expression.New(ctor, ctorArgExprs);
        var body = Expression.Convert(newExpr, typeof(object));
        var lambda = Expression.Lambda<Func<IResolver, object, object>>(body, resolverParam, innerParam);
        var compiled = lambda.Compile();
        lock (_lock)
        {
            _decoratorActivatorCache[dec] = compiled;
        }
        return compiled;
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

    /// <summary>
    /// Returns true if at least one non-decorator registration of <paramref name="serviceType"/>
    /// (closed or open-generic) is present. Used by the MEDI adapter's
    /// <c>IServiceProviderIsService.IsService</c> implementation.
    /// </summary>
    internal bool IsRegisteredInternal(Type serviceType)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (_byServiceType.TryGetValue(serviceType, out var list) && list.Count > 0)
        {
            // Any non-decorator registration is enough; the by-type bucket excludes decorators.
            return true;
        }
        if (serviceType.IsConstructedGenericType
            && _openGenericsByDef.ContainsKey(serviceType.GetGenericTypeDefinition()))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if a non-decorator registration of <paramref name="serviceType"/> with the
    /// supplied <paramref name="key"/> exists. Used by the MEDI adapter's
    /// <c>IServiceProviderIsKeyedService.IsKeyedService</c> implementation.
    /// </summary>
    internal bool IsRegisteredInternal(Type serviceType, object key)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (key is null) throw new ArgumentNullException(nameof(key));
        return FindKeyedRegistration(serviceType, key) is not null;
    }

    /// <summary>
    /// Enumerates every registration the container holds. Decorator and open-generic entries are
    /// included; callers may filter on <c>IsDecorator</c> / <c>IsOpenGeneric</c> as needed.
    /// </summary>
    internal IEnumerable<Registration> GetAllRegistrations() => _registrations;

    /// <summary>
    /// Finds a single non-decorator registration whose <c>ServiceType</c> matches
    /// <paramref name="serviceType"/> and whose <c>Key</c> equals <paramref name="key"/>. Returns
    /// the first such registration in declaration order or <c>null</c> when nothing matches.
    /// </summary>
    internal Registration? FindKeyedRegistration(Type serviceType, object key)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (key is null) throw new ArgumentNullException(nameof(key));
        foreach (var reg in _registrations)
        {
            if (reg.IsDecorator) continue;
            if (reg.IsOpenGeneric) continue;
            if (reg.ServiceType != serviceType) continue;
            if (reg.Key is null) continue;
            if (!reg.Key.Equals(key)) continue;
            return reg;
        }
        return null;
    }

    /// <summary>
    /// Resolves a keyed registration directly, honouring lifetime semantics. Returns <c>null</c>
    /// when no registration matches the supplied <paramref name="serviceType"/> + <paramref name="key"/>.
    /// </summary>
    internal object? ResolveKeyed(Type serviceType, object key, Scope? scope)
    {
        EnsureNotDisposed();
        var reg = FindKeyedRegistration(serviceType, key);
        if (reg is null) return null;
        return Activate(reg, scope, new HashSet<Type>(), new Stack<Type>());
    }

    /// <summary>
    /// Walks every non-decorator, non-open-generic registration and resolves each into a throwaway
    /// scope, accumulating failures into an <see cref="AggregateException"/>. Open-generic registrations
    /// cannot be validated without sample type arguments, so they are skipped.
    /// </summary>
    internal void ValidateAllRegistrations()
    {
        var errors = new List<Exception>();
        using var scope = (Scope)CreateScope();
        foreach (var reg in _registrations)
        {
            if (reg.IsDecorator) continue;
            if (reg.IsOpenGeneric) continue; // need closed generic args; can't validate generically.
            try
            {
                _ = Activate(reg, scope, new HashSet<Type>(), new Stack<Type>());
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
        if (errors.Count > 0)
        {
            var inner = string.Join(" | ", errors.Select(e => e.Message));
            throw new AggregateException(
                "Container validation failed: " + inner,
                errors);
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
        List<object> snapshot;
        lock (_lock)
        {
            snapshot = new List<object>(_ownedDisposables);
            _ownedDisposables.Clear();
            _singletons.Clear();
            _singletonFactories.Clear();
            _activatorCache.Clear();
            _ctorOnlyActivatorCache.Clear();
            _decoratorActivatorCache.Clear();
        }
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            DisposalHelpers.DisposeSync(snapshot[i]);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        List<object> snapshot;
        lock (_lock)
        {
            snapshot = new List<object>(_ownedDisposables);
            _ownedDisposables.Clear();
            _singletons.Clear();
            _singletonFactories.Clear();
            _activatorCache.Clear();
            _ctorOnlyActivatorCache.Clear();
            _decoratorActivatorCache.Clear();
        }
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            await DisposalHelpers.DisposeAsyncSafe(snapshot[i]).ConfigureAwait(false);
        }
    }
}
