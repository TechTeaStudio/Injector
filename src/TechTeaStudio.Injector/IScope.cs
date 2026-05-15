namespace TechTeaStudio.Injector;

/// <summary>
/// A unit of scoped resolution. Disposing the scope releases every <see cref="IDisposable"/>
/// instance that was created with <see cref="Lifetime.Scoped"/> while the scope was alive.
/// </summary>
public interface IScope : IResolver, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Begins a nested lifetime scope with additional registrations applied through
    /// <paramref name="configure"/>. Child registrations win over parent registrations for the
    /// same service type; disposing the child scope disposes only what the child owned.
    /// </summary>
    /// <param name="configure">Configuration callback used to register extra services on the child scope.</param>
    IScope BeginLifetimeScope(Action<IContainerBuilder> configure);
}
