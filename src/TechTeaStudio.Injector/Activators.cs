using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace TechTeaStudio.Injector;

/// <summary>
/// Centralised messages for the trimming / NativeAOT analyzer attributes
/// (<see cref="System.Diagnostics.CodeAnalysis.RequiresDynamicCodeAttribute"/> and
/// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>) attached to the
/// public API. Kept in one place so every annotated entry point reports the same guidance.
/// </summary>
internal static class TrimmingMessages
{
    /// <summary>
    /// Reason attached to <c>[RequiresDynamicCode]</c>: the container compiles expression trees at
    /// runtime via <c>Expression.Compile()</c>, which NativeAOT cannot support.
    /// </summary>
    internal const string DynamicCode =
        "TechTeaStudio.Injector compiles expression trees (Expression.Compile) to build per-registration activators; it is not compatible with NativeAOT. For AOT scenarios use a compile-time DI approach.";

    /// <summary>
    /// Reason attached to <c>[RequiresUnreferencedCode]</c>: the container discovers constructors,
    /// properties, and methods reflectively, so the trimmer cannot statically prove which members are used.
    /// </summary>
    internal const string UnreferencedCode =
        "TechTeaStudio.Injector uses reflection over constructors, properties, and methods at runtime; trimming may remove members it needs. It is not compatible with trimming. For AOT/trimmed scenarios use a compile-time DI approach.";
}

/// <summary>
/// Helpers used by the resolution engine to compile activators, apply property / method injection,
/// and stack decorators.
/// </summary>
internal static class Activators
{
    /// <summary>
    /// Picks the greediest public constructor whose parameters can all be resolved (or any constructor
    /// if every parameter type has a registration). Falls back to the parameterless one if present.
    /// Throws if no usable constructor exists.
    /// </summary>
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public static ConstructorInfo SelectConstructor(Type implementationType, Func<Type, bool> isResolvable)
    {
        var ctors = implementationType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .ToArray();

        if (ctors.Length == 0)
        {
            throw new InjectorException($"Type '{implementationType.FullName}' has no public constructor.");
        }

        foreach (var ctor in ctors)
        {
            if (ctor.GetParameters().All(p => isResolvable(p.ParameterType)))
            {
                return ctor;
            }
        }

        // Final fallback: any parameterless ctor.
        foreach (var ctor in ctors)
        {
            if (ctor.GetParameters().Length == 0) return ctor;
        }

        // No constructor is fully resolvable — surface the greediest one and let parameter resolution
        // throw a helpful ServiceNotRegisteredException.
        return ctors[0];
    }

    /// <summary>
    /// Enumerates settable instance properties marked with <see cref="InjectAttribute"/>.
    /// </summary>
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public static IEnumerable<PropertyInfo> GetInjectableProperties(Type t)
    {
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.GetCustomAttribute<InjectAttribute>() is null) continue;
            if (p.SetMethod is null) continue;
            yield return p;
        }
    }

    /// <summary>
    /// Enumerates public instance methods marked with <see cref="InjectAttribute"/>.
    /// </summary>
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public static IEnumerable<MethodInfo> GetInjectableMethods(Type t)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.GetCustomAttribute<InjectAttribute>() is null) continue;
            if (m.IsSpecialName) continue; // skip property accessors
            yield return m;
        }
    }

    /// <summary>
    /// Builds an expression that creates an instance via the given constructor, then applies
    /// property and method injection.
    /// </summary>
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public static Expression BuildActivatorExpression(
        Type implementationType,
        ParameterExpression resolverParam,
        Func<Type, Expression> resolveDependency,
        Func<Type, bool> isResolvable)
    {
        var ctor = SelectConstructor(implementationType, isResolvable);
        var ctorArgs = ctor.GetParameters()
            .Select(p => (Expression)Expression.Convert(resolveDependency(p.ParameterType), p.ParameterType))
            .ToArray();

        // var inst = new T(ctorArgs);
        // inst.Prop1 = (PropType)resolver.Resolve(typeof(PropType));
        // inst.MethodInject(resolver.Resolve(...));
        // return inst;
        var instanceVar = Expression.Variable(implementationType, "inst");
        var body = new List<Expression>
        {
            Expression.Assign(instanceVar, Expression.New(ctor, ctorArgs)),
        };

        foreach (var prop in GetInjectableProperties(implementationType))
        {
            var value = resolveDependency(prop.PropertyType);
            body.Add(Expression.Assign(
                Expression.MakeMemberAccess(instanceVar, prop),
                Expression.Convert(value, prop.PropertyType)));
        }

        foreach (var method in GetInjectableMethods(implementationType))
        {
            var args = method.GetParameters()
                .Select(p => (Expression)Expression.Convert(resolveDependency(p.ParameterType), p.ParameterType))
                .ToArray();
            body.Add(Expression.Call(instanceVar, method, args));
        }

        body.Add(Expression.Convert(instanceVar, typeof(object)));

        return Expression.Block(typeof(object), new[] { instanceVar }, body);
    }

    /// <summary>
    /// Builds an expression that only invokes the chosen constructor with resolved dependencies —
    /// no property or method injection. Used when lifecycle hooks must fire between construction
    /// and post-construction injection.
    /// </summary>
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public static Expression BuildConstructorOnlyExpression(
        Type implementationType,
        ParameterExpression resolverParam,
        Func<Type, Expression> resolveDependency,
        Func<Type, bool> isResolvable)
    {
        var ctor = SelectConstructor(implementationType, isResolvable);
        var ctorArgs = ctor.GetParameters()
            .Select(p => (Expression)Expression.Convert(resolveDependency(p.ParameterType), p.ParameterType))
            .ToArray();
        return Expression.Convert(Expression.New(ctor, ctorArgs), typeof(object));
    }
}
