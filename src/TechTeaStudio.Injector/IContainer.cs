namespace TechTeaStudio.Injector;

/// <summary>
/// A built dependency injection container. The container itself acts as the root
/// <see cref="IResolver"/>; transient and singleton services may be resolved directly from it,
/// scoped services must be resolved from a <see cref="IScope"/> created via <see cref="CreateScope"/>.
/// </summary>
public interface IContainer : IResolver, IDisposable
{
    /// <summary>Creates a fresh resolution scope.</summary>
    IScope CreateScope();
}
