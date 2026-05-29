using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection;

/// <summary>
/// MEDI <see cref="IServiceProviderFactory{TContainerBuilder}"/> that plugs the Injector into
/// Generic Host / ASP.NET Core via <c>hostBuilder.UseServiceProviderFactory(new InjectorServiceProviderFactory())</c>.
/// The builder type is <see cref="IContainerBuilder"/>, so the host's <c>ConfigureContainer&lt;IContainerBuilder&gt;</c>
/// hook receives the Injector's fluent registration API.
/// </summary>
public sealed class InjectorServiceProviderFactory : IServiceProviderFactory<IContainerBuilder>
{
    private readonly BuildOptions? _buildOptions;

    /// <summary>
    /// Creates a factory with default build options (lazy validation).
    /// </summary>
    public InjectorServiceProviderFactory()
        : this(buildOptions: null)
    {
    }

    /// <summary>
    /// Creates a factory that builds the container with the supplied <paramref name="buildOptions"/>.
    /// Pass <c>new BuildOptions { ValidateOnBuild = true }</c> to fail-fast at host build time when
    /// any registration cannot be resolved.
    /// </summary>
    /// <param name="buildOptions">Build-time options forwarded to <see cref="IContainerBuilder.Build(BuildOptions)"/>.
    /// When <c>null</c>, the parameterless <see cref="IContainerBuilder.Build()"/> overload is used.</param>
    public InjectorServiceProviderFactory(BuildOptions? buildOptions)
    {
        _buildOptions = buildOptions;
    }

    /// <summary>
    /// Creates an <see cref="IContainerBuilder"/> and seeds it with every <see cref="ServiceDescriptor"/>
    /// in <paramref name="services"/>. The host may invoke additional <c>ConfigureContainer&lt;IContainerBuilder&gt;</c>
    /// callbacks on the returned builder before <see cref="CreateServiceProvider"/> is called.
    /// </summary>
    /// <param name="services">Source service collection from the host.</param>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "Implements IServiceProviderFactory<IContainerBuilder>.CreateBuilder, which carries no trimming annotations upstream. Seeding the builder is not trim-safe; documented on the package and on the core's [RequiresUnreferencedCode] entry points.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Implements IServiceProviderFactory<IContainerBuilder>.CreateBuilder, which carries no AOT annotations upstream. Seeding the builder compiles factory delegates and is not AOT-safe; documented on the package and on the core's [RequiresDynamicCode] entry points.")]
    public IContainerBuilder CreateBuilder(IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        return builder;
    }

    /// <summary>
    /// Builds <paramref name="containerBuilder"/> into an <see cref="IContainer"/> and wraps it in
    /// an <see cref="InjectorServiceProvider"/>. The returned provider owns the container — it will
    /// dispose the container when the host disposes the provider.
    /// </summary>
    /// <param name="containerBuilder">Builder produced by <see cref="CreateBuilder"/> (and possibly
    /// mutated by host configuration callbacks).</param>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification = "Implements IServiceProviderFactory<IContainerBuilder>.CreateServiceProvider, which carries no trimming annotations upstream. Building the container uses reflection and is not trim-safe; documented on the package and on the core's [RequiresUnreferencedCode] entry points.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Implements IServiceProviderFactory<IContainerBuilder>.CreateServiceProvider, which carries no AOT annotations upstream. Building the container compiles expression trees and is not AOT-safe; documented on the package and on the core's [RequiresDynamicCode] entry points.")]
    public IServiceProvider CreateServiceProvider(IContainerBuilder containerBuilder)
    {
        if (containerBuilder is null) throw new ArgumentNullException(nameof(containerBuilder));
        var container = _buildOptions is null
            ? containerBuilder.Build()
            : containerBuilder.Build(_buildOptions);
        return new InjectorServiceProvider(container, ownsContainer: true);
    }
}
