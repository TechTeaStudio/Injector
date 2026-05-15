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

    public bool HasAnyRegistrationsFor(Type serviceType)
    {
        if (_byServiceType.ContainsKey(serviceType)) return true;
        if (serviceType.IsConstructedGenericType && _openGenericsByDef.ContainsKey(serviceType.GetGenericTypeDefinition())) return true;
        return false;
    }
}
