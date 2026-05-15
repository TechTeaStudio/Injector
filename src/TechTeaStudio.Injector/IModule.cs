namespace TechTeaStudio.Injector;

/// <summary>
/// A reusable bundle of registrations. Implement this on a class to package related services
/// (for example, "all repositories" or "logging providers") behind a single
/// <see cref="IContainerBuilder.RegisterModule(IModule)"/> call.
/// </summary>
public interface IModule
{
    /// <summary>
    /// Adds this module's registrations to the supplied <paramref name="builder"/>.
    /// </summary>
    void Register(IContainerBuilder builder);
}
