namespace TechTeaStudio.Injector;

/// <summary>
/// Holds the extra registrations attached to a child scope by
/// <see cref="IScope.BeginLifetimeScope(Action{IContainerBuilder})"/>. A child scope answers
/// resolves by consulting this registry first; misses delegate to the parent / container.
/// </summary>
internal sealed class ChildScopeRegistry
{
    private readonly Dictionary<Type, List<Registration>> _byServiceType = new();
    private readonly Dictionary<Type, List<Registration>> _openGenericsByDef = new();
    private readonly List<Registration> _all = new();

    private ChildScopeRegistry() { }

    public static ChildScopeRegistry From(IReadOnlyList<Registration> registrations)
    {
        var r = new ChildScopeRegistry();
        foreach (var reg in registrations)
        {
            r._all.Add(reg);
            if (reg.IsDecorator) continue;
            if (reg.IsOpenGeneric)
            {
                if (!r._openGenericsByDef.TryGetValue(reg.ServiceType, out var openList))
                {
                    openList = new List<Registration>();
                    r._openGenericsByDef[reg.ServiceType] = openList;
                }
                openList.Add(reg);
                continue;
            }
            if (!r._byServiceType.TryGetValue(reg.ServiceType, out var list))
            {
                list = new List<Registration>();
                r._byServiceType[reg.ServiceType] = list;
            }
            list.Add(reg);
        }
        return r;
    }

    public IReadOnlyList<Registration> All => _all;

    public bool TryGet(Type serviceType, out List<Registration>? regs)
    {
        if (_byServiceType.TryGetValue(serviceType, out var list))
        {
            regs = list;
            return true;
        }
        regs = null;
        return false;
    }

    public bool TryGetOpenGeneric(Type genericDef, out List<Registration>? regs)
    {
        if (_openGenericsByDef.TryGetValue(genericDef, out var list))
        {
            regs = list;
            return true;
        }
        regs = null;
        return false;
    }

    /// <summary>
    /// True when an unkeyed registration (closed or open-generic) can answer an unkeyed resolve of
    /// <paramref name="serviceType"/>. Keyed registrations are reachable only by key.
    /// </summary>
    public bool HasAnyRegistrationsFor(Type serviceType)
    {
        if (_byServiceType.TryGetValue(serviceType, out var list) && list.Exists(static r => r.Key is null)) return true;
        if (serviceType.IsConstructedGenericType
            && _openGenericsByDef.TryGetValue(serviceType.GetGenericTypeDefinition(), out var openList)
            && openList.Exists(static r => r.Key is null))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the keyed registration for <paramref name="serviceType"/> + <paramref name="key"/>: a
    /// closed one first, otherwise an open-generic one on the matching definition (returned
    /// unclosed; the caller synthesizes the closed registration). <c>null</c> when nothing matches.
    /// </summary>
    public Registration? FindKeyed(Type serviceType, object key)
    {
        if (_byServiceType.TryGetValue(serviceType, out var list))
        {
            var closed = list.Find(r => r.Key is not null && r.Key.Equals(key));
            if (closed is not null) return closed;
        }
        if (serviceType.IsConstructedGenericType
            && _openGenericsByDef.TryGetValue(serviceType.GetGenericTypeDefinition(), out var openList))
        {
            return openList.Find(r => r.Key is not null && r.Key.Equals(key));
        }
        return null;
    }
}
