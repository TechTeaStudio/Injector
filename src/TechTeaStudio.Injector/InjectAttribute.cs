namespace TechTeaStudio.Injector;

/// <summary>
/// Marks a settable property or a parameterless-after-instantiation method for post-construction
/// injection by the container. Properties are written after the constructor runs; methods are
/// invoked with their parameters resolved from the container.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class InjectAttribute : Attribute
{
}
