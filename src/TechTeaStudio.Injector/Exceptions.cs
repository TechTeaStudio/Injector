namespace TechTeaStudio.Injector;

/// <summary>
/// Base type for every exception thrown by <c>TechTeaStudio.Injector</c>.
/// </summary>
public class InjectorException : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public InjectorException(string message) : base(message) { }

    /// <summary>Initializes a new instance with the given message and inner exception.</summary>
    public InjectorException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by <see cref="IResolver"/> when the requested service type has no registration.
/// </summary>
public sealed class ServiceNotRegisteredException : InjectorException
{
    /// <summary>The service type that could not be resolved.</summary>
    public Type ServiceType { get; }

    /// <summary>Initializes a new instance for the given service type.</summary>
    public ServiceNotRegisteredException(Type serviceType)
        : base($"No registration found for service type '{serviceType.FullName}'.")
    {
        ServiceType = serviceType;
    }
}

/// <summary>
/// Thrown when the resolver detects a cycle while compiling a registration's activator.
/// </summary>
public sealed class CircularDependencyException : InjectorException
{
    /// <summary>The ordered chain of service types that form the cycle.</summary>
    public IReadOnlyList<Type> Chain { get; }

    /// <summary>Initializes a new instance for the given dependency chain.</summary>
    public CircularDependencyException(IReadOnlyList<Type> chain)
        : base("Circular dependency detected: " + string.Join(" -> ", chain.Select(t => t.FullName)))
    {
        Chain = chain;
    }
}

/// <summary>
/// Thrown when the container builder receives an invalid configuration
/// (for example, a decorator registered before the service it decorates).
/// </summary>
public sealed class RegistrationException : InjectorException
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public RegistrationException(string message) : base(message) { }
}
