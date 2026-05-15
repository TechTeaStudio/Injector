namespace TechTeaStudio.Injector;

/// <summary>
/// Determines how the container manages the lifetime of a registered service.
/// </summary>
public enum Lifetime
{
    /// <summary>A new instance is created every time the service is resolved.</summary>
    Transient = 0,

    /// <summary>A single instance is created per <see cref="IScope"/>.</summary>
    Scoped = 1,

    /// <summary>A single instance is created and shared across the entire container.</summary>
    Singleton = 2,
}
