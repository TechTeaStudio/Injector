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

/// <summary>
/// Thrown when scope validation (<see cref="BuildOptions.ValidateScopes"/>) detects a captive
/// dependency: a singleton service that captures a shorter-lived <see cref="Lifetime.Scoped"/>
/// dependency through its constructor graph.
/// </summary>
public sealed class CaptiveDependencyException : InjectorException
{
    /// <summary>The singleton service type that would capture the scoped dependency.</summary>
    public Type SingletonType { get; }

    /// <summary>The scoped service type that gets captured by the singleton.</summary>
    public Type CapturedScopedType { get; }

    /// <summary>The ordered chain of service types from the singleton down to the captured scoped service.</summary>
    public IReadOnlyList<Type> DependencyPath { get; }

    /// <summary>Initializes a new instance for the given singleton, captured scoped type, and dependency path.</summary>
    public CaptiveDependencyException(Type singletonType, Type capturedScopedType, IReadOnlyList<Type> dependencyPath)
        : base($"Captive dependency detected: singleton '{singletonType.FullName}' depends on scoped service "
            + $"'{capturedScopedType.FullName}' via [{string.Join(" -> ", dependencyPath.Select(t => t.FullName))}]. "
            + "A singleton outlives every scope, so it would capture and reuse a single scoped instance for the "
            + "lifetime of the container.")
    {
        SingletonType = singletonType;
        CapturedScopedType = capturedScopedType;
        DependencyPath = dependencyPath;
    }
}
