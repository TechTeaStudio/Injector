namespace TechTeaStudio.Injector;

/// <summary>
/// Options that tweak how <see cref="IContainerBuilder.Build(BuildOptions)"/> produces the container.
/// </summary>
public sealed class BuildOptions
{
    /// <summary>
    /// When <c>true</c>, the builder iterates every non-decorator registration after compilation
    /// and resolves it into a throwaway scope. Any failures are collected and thrown as a single
    /// <see cref="AggregateException"/>. When <c>false</c> (the default), registrations are only
    /// validated lazily, at the first <c>Resolve</c> call.
    /// </summary>
    public bool ValidateOnBuild { get; init; }
}
