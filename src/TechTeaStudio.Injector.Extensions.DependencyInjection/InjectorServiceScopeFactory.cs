using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection;

/// <summary>
/// MEDI <see cref="IServiceScopeFactory"/> over a wrapped <see cref="IContainer"/>. Each call to
/// <see cref="CreateScope"/> spins up a fresh <see cref="IScope"/> and wraps it in an
/// <see cref="InjectorServiceScope"/>.
/// </summary>
public sealed class InjectorServiceScopeFactory : IServiceScopeFactory
{
    private readonly IContainer _container;

    /// <summary>Wraps <paramref name="container"/> as a scope factory.</summary>
    public InjectorServiceScopeFactory(IContainer container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    /// <inheritdoc />
    public IServiceScope CreateScope() => new InjectorServiceScope(_container.CreateScope(), _container);
}
