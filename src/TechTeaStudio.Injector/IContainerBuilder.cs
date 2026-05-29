using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TechTeaStudio.Injector;

/// <summary>
/// Fluent API used to register services before building an <see cref="IContainer"/>.
/// </summary>
public interface IContainerBuilder
{
    /// <summary>
    /// Registers a concrete implementation type for the given service contract.
    /// </summary>
    /// <typeparam name="TService">Service contract.</typeparam>
    /// <typeparam name="TImpl">Concrete implementation. Must be instantiable and assignable to <typeparamref name="TService"/>.</typeparam>
    IRegistrationBuilder Register<TService, TImpl>()
        where TImpl : class, TService
        where TService : class;

    /// <summary>
    /// Registers a concrete type that doubles as its own service contract.
    /// </summary>
    /// <typeparam name="TService">Concrete type to register.</typeparam>
    IRegistrationBuilder Register<TService>() where TService : class;

    /// <summary>
    /// Registers a concrete implementation type for the given service contract using runtime
    /// <see cref="Type"/> references. Required for open-generic registrations such as
    /// <c>Register(typeof(IRepository&lt;&gt;), typeof(Repository&lt;&gt;))</c>.
    /// </summary>
    /// <param name="serviceType">Service contract (may be an open generic type definition).</param>
    /// <param name="implementationType">Concrete implementation (may be an open generic type definition).</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IRegistrationBuilder Register(Type serviceType, Type implementationType);

    /// <summary>
    /// Registers a concrete type that doubles as its own service contract using a runtime
    /// <see cref="Type"/> reference. Required for open-generic self-registrations such as
    /// <c>Register(typeof(Repository&lt;&gt;))</c>.
    /// </summary>
    /// <param name="serviceType">Concrete type to register (may be an open generic type definition).</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IRegistrationBuilder Register(Type serviceType);

    /// <summary>
    /// Registers a factory delegate that produces instances of <typeparamref name="TService"/>.
    /// The factory receives the active <see cref="IResolver"/> so it may pull in collaborators.
    /// </summary>
    /// <typeparam name="TService">Service contract.</typeparam>
    /// <param name="factory">Factory delegate.</param>
    IRegistrationBuilder Register<TService>(Func<IResolver, TService> factory) where TService : class;

    /// <summary>
    /// Registers an already-created instance under <typeparamref name="TService"/>; lifetime is
    /// implicitly <see cref="Lifetime.Singleton"/> and the container will dispose the instance
    /// if it implements <see cref="IDisposable"/>.
    /// </summary>
    /// <typeparam name="TService">Service contract.</typeparam>
    /// <param name="instance">Pre-built instance.</param>
    IRegistrationBuilder RegisterInstance<TService>(TService instance) where TService : class;

    /// <summary>
    /// Registers a decorator that wraps every existing registration of <typeparamref name="TService"/>.
    /// Decorators are applied in registration order — the last registered decorator is outermost.
    /// </summary>
    /// <typeparam name="TService">Service contract being decorated.</typeparam>
    /// <typeparam name="TDecorator">Decorator type. Must accept the service in its constructor.</typeparam>
    IContainerBuilder RegisterDecorator<TService, TDecorator>()
        where TDecorator : class, TService
        where TService : class;

    /// <summary>
    /// Invokes <see cref="IModule.Register(IContainerBuilder)"/> on the supplied module.
    /// </summary>
    /// <param name="module">Module to install.</param>
    IContainerBuilder RegisterModule(IModule module);

    /// <summary>
    /// Scans the supplied assembly for concrete, non-abstract, public types and registers each one
    /// that satisfies <paramref name="filter"/> (or every such type when <paramref name="filter"/> is <c>null</c>).
    /// Each discovered type is registered against itself and every interface it implements.
    /// </summary>
    /// <param name="asm">Assembly to scan.</param>
    /// <param name="filter">Optional predicate that decides whether a candidate type is registered.</param>
    /// <param name="lifetime">Lifetime for the registrations. Defaults to <see cref="Lifetime.Transient"/>.</param>
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IContainerBuilder RegisterAssembly(Assembly asm, Predicate<Type>? filter = null, Lifetime lifetime = Lifetime.Transient);

    /// <summary>
    /// Compiles every registration and returns the built <see cref="IContainer"/>. The builder
    /// must not be used after <see cref="Build()"/> is called.
    /// </summary>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IContainer Build();

    /// <summary>
    /// Compiles every registration and returns the built <see cref="IContainer"/>, applying the
    /// supplied <paramref name="options"/>. The builder must not be used after this call.
    /// </summary>
    /// <param name="options">Build-time options. When <c>ValidateOnBuild</c> is true, every registration
    /// is resolved into a throwaway scope and failures are aggregated into a single exception.</param>
    [RequiresDynamicCode(TrimmingMessages.DynamicCode)]
    [RequiresUnreferencedCode(TrimmingMessages.UnreferencedCode)]
    IContainer Build(BuildOptions options);
}

/// <summary>
/// Returned by every registration method on <see cref="IContainerBuilder"/>. Lets the caller pick
/// a lifetime, name the registration, or attach a key (for <c>ResolveDictionary</c>).
/// </summary>
public interface IRegistrationBuilder
{
    /// <summary>Sets the registration's lifetime to <see cref="Lifetime.Transient"/>.</summary>
    IRegistrationBuilder AsTransient();

    /// <summary>Sets the registration's lifetime to <see cref="Lifetime.Scoped"/>.</summary>
    IRegistrationBuilder AsScoped();

    /// <summary>Sets the registration's lifetime to <see cref="Lifetime.Singleton"/>.</summary>
    IRegistrationBuilder AsSingleton();

    /// <summary>
    /// Tags this registration with <paramref name="key"/> so it is reachable via
    /// <see cref="IResolver.ResolveDictionary{TKey,T}"/>. Keys must be unique per service type.
    /// </summary>
    /// <param name="key">Lookup key.</param>
    IRegistrationBuilder WithKey(object key);

    /// <summary>
    /// Registers the same registration under one or more additional service contracts.
    /// Useful when an implementation satisfies multiple interfaces.
    /// </summary>
    /// <param name="additionalServiceTypes">Extra service contracts to bind.</param>
    IRegistrationBuilder As(params Type[] additionalServiceTypes);

    /// <summary>
    /// Adds a callback that runs immediately after the constructor returns, before property /
    /// method injection. The callback receives the raw instance (cast to the implementation type
    /// by the caller).
    /// </summary>
    /// <param name="callback">Hook invoked on the raw instance.</param>
    IRegistrationBuilder OnActivating(Action<object> callback);

    /// <summary>
    /// Adds a callback that runs after property / method injection completes, immediately before
    /// the resolver returns the instance to the caller.
    /// </summary>
    /// <param name="callback">Hook invoked on the fully-initialized instance.</param>
    IRegistrationBuilder OnActivated(Action<object> callback);
}
