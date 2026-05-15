namespace TechTeaStudio.Injector;

/// <summary>
/// A unit of scoped resolution. Disposing the scope releases every <see cref="IDisposable"/>
/// instance that was created with <see cref="Lifetime.Scoped"/> while the scope was alive.
/// </summary>
public interface IScope : IResolver, IDisposable
{
}
