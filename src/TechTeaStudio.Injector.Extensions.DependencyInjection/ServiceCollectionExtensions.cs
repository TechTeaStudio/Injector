using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection;

/// <summary>
/// Bridges <see cref="IServiceCollection"/> and <see cref="IContainerBuilder"/>, plus exposes a
/// built <see cref="IContainer"/> as an <see cref="IServiceProvider"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Translates every <see cref="ServiceDescriptor"/> in <paramref name="services"/> into a
    /// matching call on <paramref name="builder"/>. Supports all six lifetime × source combinations
    /// (Singleton / Scoped / Transient ✕ ImplementationType / ImplementationInstance /
    /// ImplementationFactory) plus their keyed variants.
    /// </summary>
    /// <param name="builder">Target container builder.</param>
    /// <param name="services">Source service collection.</param>
    /// <returns>The same <paramref name="builder"/> for fluent chaining.</returns>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    public static IContainerBuilder PopulateFromServiceCollection(this IContainerBuilder builder, IServiceCollection services)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        if (services is null) throw new ArgumentNullException(nameof(services));

        foreach (var descriptor in services)
        {
            ApplyDescriptor(builder, descriptor);
        }
        return builder;
    }

    /// <summary>
    /// Adapts <paramref name="container"/> to <see cref="IServiceProvider"/>. The returned provider
    /// also implements <see cref="IServiceProviderIsService"/>, <see cref="IKeyedServiceProvider"/>,
    /// <see cref="IDisposable"/>, and <see cref="IAsyncDisposable"/>.
    /// </summary>
    /// <param name="container">Container to adapt.</param>
    public static IServiceProvider AsServiceProvider(this IContainer container)
    {
        if (container is null) throw new ArgumentNullException(nameof(container));
        return new InjectorServiceProvider(container);
    }

    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    private static void ApplyDescriptor(IContainerBuilder builder, ServiceDescriptor descriptor)
    {
        var isKeyed = descriptor.IsKeyedService;
        var serviceType = descriptor.ServiceType;
        var lifetime = MapLifetime(descriptor.Lifetime);

        IRegistrationBuilder regBuilder;

        if (isKeyed)
        {
            if (descriptor.KeyedImplementationType is not null)
            {
                regBuilder = builder.Register(serviceType, descriptor.KeyedImplementationType);
            }
            else if (descriptor.KeyedImplementationInstance is not null)
            {
                regBuilder = RegisterInstanceNonGeneric(builder, serviceType, descriptor.KeyedImplementationInstance);
            }
            else if (descriptor.KeyedImplementationFactory is not null)
            {
                var key = descriptor.ServiceKey;
                var medFactory = descriptor.KeyedImplementationFactory;
                regBuilder = RegisterFactoryNonGeneric(
                    builder,
                    serviceType,
                    resolver => medFactory(ResolverAsServiceProvider(resolver), key));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Keyed ServiceDescriptor for '{serviceType.FullName}' has no implementation type, instance, or factory.");
            }
        }
        else
        {
            if (descriptor.ImplementationType is not null)
            {
                regBuilder = builder.Register(serviceType, descriptor.ImplementationType);
            }
            else if (descriptor.ImplementationInstance is not null)
            {
                regBuilder = RegisterInstanceNonGeneric(builder, serviceType, descriptor.ImplementationInstance);
            }
            else if (descriptor.ImplementationFactory is not null)
            {
                var medFactory = descriptor.ImplementationFactory;
                regBuilder = RegisterFactoryNonGeneric(
                    builder,
                    serviceType,
                    resolver => medFactory(ResolverAsServiceProvider(resolver)));
            }
            else
            {
                throw new InvalidOperationException(
                    $"ServiceDescriptor for '{serviceType.FullName}' has no implementation type, instance, or factory.");
            }
        }

        ApplyLifetime(regBuilder, lifetime);

        if (isKeyed && descriptor.ServiceKey is not null)
        {
            regBuilder.WithKey(descriptor.ServiceKey);
        }
    }

    private static Lifetime MapLifetime(ServiceLifetime lifetime) => lifetime switch
    {
        ServiceLifetime.Singleton => Lifetime.Singleton,
        ServiceLifetime.Scoped => Lifetime.Scoped,
        ServiceLifetime.Transient => Lifetime.Transient,
        _ => throw new InvalidOperationException($"Unknown ServiceLifetime '{lifetime}'."),
    };

    private static void ApplyLifetime(IRegistrationBuilder regBuilder, Lifetime lifetime)
    {
        switch (lifetime)
        {
            case Lifetime.Singleton: regBuilder.AsSingleton(); break;
            case Lifetime.Scoped: regBuilder.AsScoped(); break;
            case Lifetime.Transient: regBuilder.AsTransient(); break;
        }
    }

    // RegisterInstance<T> is generic; we have an `object` and a `Type` at runtime.
    // Wrap the instance in a singleton factory.
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    private static IRegistrationBuilder RegisterInstanceNonGeneric(IContainerBuilder builder, Type serviceType, object instance)
    {
        return RegisterFactoryNonGeneric(builder, serviceType, _ => instance).AsSingleton();
    }

    // Register<T>(Func<IResolver, T>) is generic; we have a `Type` at runtime.
    // The core library exposes `Register(Type, Type)` for type-to-type — but not Register(Type, factory).
    // Use reflection to call the generic factory overload.
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    private static IRegistrationBuilder RegisterFactoryNonGeneric(
        IContainerBuilder builder, Type serviceType, Func<IResolver, object> factory)
    {
        // Build a Func<IResolver, TService> where TService = serviceType, that returns `factory(r)`.
        // Use Register<TService>(Func<IResolver, TService>) via reflection.
        var registerMethod = typeof(IContainerBuilder)
            .GetMethods()
            .Single(m =>
                m.Name == nameof(IContainerBuilder.Register)
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.IsGenericType
                && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>));

        var closed = registerMethod.MakeGenericMethod(serviceType);
        var funcType = typeof(Func<,>).MakeGenericType(typeof(IResolver), serviceType);

        // Build a strongly-typed factory delegate via Expression.
        var resolverParam = System.Linq.Expressions.Expression.Parameter(typeof(IResolver), "r");
        var factoryConst = System.Linq.Expressions.Expression.Constant(factory);
        var invokeFactory = System.Linq.Expressions.Expression.Invoke(factoryConst, resolverParam);
        var converted = System.Linq.Expressions.Expression.Convert(invokeFactory, serviceType);
        var lambda = System.Linq.Expressions.Expression.Lambda(funcType, converted, resolverParam).Compile();

        var result = closed.Invoke(builder, new object[] { lambda });
        return (IRegistrationBuilder)result!;
    }

    internal static IServiceProvider ResolverAsServiceProvider(IResolver resolver)
    {
        // The resolver might be the Container or a Scope. Wrap accordingly.
        if (resolver is IContainer container) return new InjectorServiceProvider(container);
        if (resolver is IScope scope) return new ScopedInjectorServiceProvider(scope, root: null);
        // Fallback: a generic IResolver wrapper that mirrors GetService through it.
        return new ResolverServiceProvider(resolver);
    }
}
