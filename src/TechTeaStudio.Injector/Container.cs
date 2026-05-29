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
    private readonly Dictionary<Type, List<Registration>> _byServiceType;
    private readonly Dictionary<Type, List<Registration>> _openGenericsByDef;
    private readonly Dictionary<Registration, Func<IResolver, object>> _activatorCache = new();
    private readonly Dictionary<Registration, object> _singletons = new();
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
        _byServiceType = new Dictionary<Type, List<Registration>>();
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

    // --- Public keyed resolution (delegates to the internal ResolveKeyed(Type, object, Scope?)). ---

    /// <inheritdoc />
    public T ResolveKeyed<T>(object key) => (T)ResolveKeyed(typeof(T), key);

    /// <inheritdoc />
    public object ResolveKeyed(Type serviceType, object key) => ResolveKeyedOrThrow(serviceType, key, scope: null);

    /// <inheritdoc />
    public bool TryResolveKeyed<T>(object key, out T value)
    {
        if (TryResolveKeyedInternal(typeof(T), key, scope: null, out var resolved))
        {
            value = (T)resolved!;
            return true;
        }
        value = default!;
        return false;
    }

    // --- Non-throwing / optional resolution. ---

    /// <inheritdoc />
    public bool TryResolve<T>(out T value)
    {
        if (TryResolveInternal(typeof(T), scope: null, out var resolved))
        {
            value = (T)resolved!;
            return true;
        }
        value = default!;
        return false;
    }

    /// <inheritdoc />
    public bool TryResolve(Type serviceType, out object? value)
        => TryResolveInternal(serviceType, scope: null, out value);

    /// <inheritdoc />
    public T? ResolveOptional<T>()
        => TryResolveInternal(typeof(T), scope: null, out var resolved) ? (T?)resolved : default;

    /// <inheritdoc />
    public object? ResolveOptional(Type serviceType)
        => TryResolveInternal(serviceType, scope: null, out var resolved) ? resolved : null;

    // --- Public registration probe (delegates to IsRegisteredInternal). ---

    /// <inheritdoc />
    public bool IsRegistered<T>() => IsRegisteredInternal(typeof(T));

    /// <inheritdoc />
    public bool IsRegistered(Type serviceType) => IsRegisteredInternal(serviceType);

    /// <summary>
    /// Shared keyed-resolve-or-throw used by both the container root and scopes. Delegates to the
    /// internal <see cref="ResolveKeyed(Type, object, Scope?)"/> and converts a <c>null</c> miss
    /// into a descriptive exception that names the key.
    /// </summary>
    internal object ResolveKeyedOrThrow(Type serviceType, object key, Scope? scope)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (key is null) throw new ArgumentNullException(nameof(key));
        var resolved = ResolveKeyed(serviceType, key, scope);
        if (resolved is null)
        {
            throw new ServiceNotRegisteredException(serviceType);
        }
        return resolved;
    }

    /// <summary>
    /// Shared keyed try-resolve used by both the container root and scopes. Returns <c>false</c> only
    /// when no keyed registration matches; a genuine construction failure still propagates.
    /// </summary>
    internal bool TryResolveKeyedInternal(Type serviceType, object key, Scope? scope, out object? value)
    {
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (key is null) throw new ArgumentNullException(nameof(key));
        // FindKeyedRegistration is the same lookup ResolveKeyed performs; checking first keeps a
        // genuine activation failure (thrown by Activate) propagating instead of being swallowed.
        if (FindKeyedRegistration(serviceType, key) is null)
        {
            value = null;
            return false;
        }
        value = ResolveKeyed(serviceType, key, scope);
        return true;
    }

    /// <summary>
    /// Shared non-throwing resolve used by both the container root and scopes. Returns <c>false</c>
    /// only when the top-level <paramref name="serviceType"/> is not resolvable at all (no
    /// registration, not a synthetic Lazy/Func/Owned/collection, not a built-in resolver service).
    /// When the service IS resolvable but its construction fails, the exception propagates.
    /// </summary>
    internal bool TryResolveInternal(Type serviceType, Scope? scope, out object? value)
    {
        EnsureNotDisposed();
        if (serviceType is null) throw new ArgumentNullException(nameof(serviceType));
        if (!CanResolveTopLevel(serviceType, scope))
        {
            value = null;
            return false;
        }
        value = ResolveInternal(serviceType, scope);
        return true;
    }

    /// <summary>
    /// Mirrors the conditions under which <see cref="ResolveInternal"/> would throw
    /// <see cref="ServiceNotRegisteredException"/> for the top-level type: returns <c>true</c> when a
    /// registration exists (closed or open-generic), when the type is a built-in collection /
    /// Lazy / Func / Owned synthetic, when a scope in the chain carries an extra registration, or when
    /// the type is one of the ambient resolver services. This is a check-then-resolve guard so that
    /// genuine downstream resolution failures are never swallowed.
    /// </summary>
    private bool CanResolveTopLevel(Type serviceType, Scope? scope)
    {
        // Registered directly (closed) or via an open-generic definition.
        if (IsRegisteredInternal(serviceType)) return true;

        // Ambient resolver services that ResolveInternal hands back without a registration.
        if (serviceType == typeof(IResolver) || serviceType == typeof(IScope) || serviceType == typeof(IContainer))
        {
            return true;
        }

        // A child scope in the chain may carry an extra registration for this type.
        if (scope is not null && ScopeChainHas(serviceType, scope)) return true;

        // Built-in collection / synthetic generic support (IEnumerable<>, IReadOnly*, IDictionary<>,
        // Lazy<>, Func<...>, Owned<>) — these always resolve even with zero user registrations.
        if (serviceType.IsGenericType)
        {
            var def = serviceType.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyCollection<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>)
                || def == typeof(Lazy<>)
                || def == typeof(Owned<>)
                || (def.FullName is { } name && name.StartsWith("System.Func`", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when any scope in the chain rooted at <paramref name="scope"/> carries an
    /// extra (child-only) registration for <paramref name="serviceType"/> — closed or open-generic.
    /// </summary>
    private static bool ScopeChainHas(Type serviceType, Scope scope)
    {
        Scope? current = scope;
        while (current is not null)
        {
            if (current.Extra is { } extra && extra.HasAnyRegistrationsFor(serviceType)) return true;
            current = current.Parent;
        }
        return false;
    }

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
        if (_byServiceType.TryGetValue(serviceType, out var list)) return list;

        // No closed registration; try open-generic fallback.
        if (serviceType.IsConstructedGenericType)
        {
            var def = serviceType.GetGenericTypeDefinition();
            if (_openGenericsByDef.TryGetValue(def, out var openList))
            {
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

    // Pre-indexed decorator lookup, built once (lazily, thread-safe) from _registrations. Closed
    // decorators are keyed by their exact ServiceType; open-generic decorators by their generic type
    // definition. Replaces the former O(n) scan of _registrations on every activation with an O(1)
    // dictionary probe. Built lazily (via LazyInitializer) so the whole feature stays inside the
    // decorator subsystem and never touches the shared constructor / field block.
    private DecoratorIndex? _decoratorIndex;
    private object _decoratorIndexLock = new();

    /// <summary>
    /// Returns the decorators that apply to <paramref name="serviceType"/>, in registration order.
    /// A closed service type picks up decorators registered on the exact type plus any registered on
    /// the matching open-generic definition (so a decorator on <c>IRepository&lt;&gt;</c> wraps a
    /// resolved <c>IRepository&lt;User&gt;</c>). The merge preserves declaration order across both
    /// kinds so the decorator chain (innermost-first) is deterministic.
    /// </summary>
    private List<Registration> GetDecorators(Type serviceType)
    {
        var index = System.Threading.LazyInitializer.EnsureInitialized(
            ref _decoratorIndex, ref _decoratorIndexLock, () => new DecoratorIndex(_registrations))!;
        var closed = index.GetClosed(serviceType);

        if (!serviceType.IsConstructedGenericType)
        {
            return closed.Count == 0 ? EmptyDecorators : closed;
        }

        var open = index.GetOpen(serviceType.GetGenericTypeDefinition());
        if (open.Count == 0) return closed.Count == 0 ? EmptyDecorators : closed;
        if (closed.Count == 0) return open;

        // Merge closed + open decorators, ordered by registration Order so chaining is deterministic.
        var merged = new List<Registration>(closed.Count + open.Count);
        merged.AddRange(closed);
        merged.AddRange(open);
        merged.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return merged;
    }

    private static readonly List<Registration> EmptyDecorators = new();

    /// <summary>
    /// Immutable, build-once lookup of decorator registrations split into closed (keyed by exact
    /// service type) and open-generic (keyed by generic type definition) buckets, each kept in
    /// registration order.
    /// </summary>
    private sealed class DecoratorIndex
    {
        private static readonly List<Registration> Empty = new();
        private readonly Dictionary<Type, List<Registration>> _closed = new();
        private readonly Dictionary<Type, List<Registration>> _open = new();

        public DecoratorIndex(IEnumerable<Registration> registrations)
        {
            foreach (var reg in registrations)
            {
                if (!reg.IsDecorator) continue;
                var bucket = reg.ServiceType.IsGenericTypeDefinition ? _open : _closed;
                if (!bucket.TryGetValue(reg.ServiceType, out var list))
                {
                    list = new List<Registration>();
                    bucket[reg.ServiceType] = list;
                }
                list.Add(reg);
            }
        }

        public List<Registration> GetClosed(Type serviceType)
            => _closed.TryGetValue(serviceType, out var list) ? list : Empty;

        public List<Registration> GetOpen(Type genericDefinition)
            => _open.TryGetValue(genericDefinition, out var list) ? list : Empty;
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
                        if (DisposalHelpers.IsTrackable(singleton)) _ownedDisposables.Add(singleton);
                        return singleton;
                    }
                    // Lost the race — drop our duplicate (potentially disposing it).
                    if (!ReferenceEquals(raceWinner, singleton) && DisposalHelpers.IsTrackable(singleton))
                    {
                        DisposalHelpers.DisposeSync(singleton);
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

    // Compiled decorator activators, keyed by the (decorator registration, closed service type) pair.
    // The closed service type is part of the key so a single open-generic decorator registration
    // (e.g. on IRepository<>) caches one compiled activator per closed service it wraps
    // (IRepository<User>, IRepository<Order>, ...). A ConcurrentDictionary gives lock-free reads.
    private readonly ConcurrentDictionary<(Registration Decorator, Type ServiceType), Func<IResolver, object, object>> _decoratorActivatorCache = new();

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
        => _decoratorActivatorCache.GetOrAdd((dec, serviceType), static (key, self) => self.CompileDecoratorActivator(key.Decorator, key.ServiceType), this);

    private Func<IResolver, object, object> CompileDecoratorActivator(Registration dec, Type serviceType)
    {
        var decType = dec.DecoratorType
            ?? throw new InjectorException($"Decorator registration for '{dec.ServiceType.FullName}' has no decorator type.");

        // Open-generic decorator (registered on e.g. IRepository<>): close the decorator
        // implementation with the service's generic arguments so it matches the closed service type.
        if (decType.IsGenericTypeDefinition)
        {
            if (!serviceType.IsConstructedGenericType)
            {
                throw new InjectorException(
                    $"Open-generic decorator '{decType.FullName}' cannot be applied to non-generic service '{serviceType.FullName}'.");
            }
            try
            {
                decType = decType.MakeGenericType(serviceType.GenericTypeArguments);
            }
            catch (ArgumentException ex)
            {
                throw new InjectorException(
                    $"Cannot close open-generic decorator '{decType.FullName}' for service '{serviceType.FullName}': {ex.Message}",
                    ex);
            }
        }

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
        return lambda.Compile();
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
