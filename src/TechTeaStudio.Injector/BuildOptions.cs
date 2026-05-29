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

    /// <summary>
    /// When <c>true</c>, the builder statically checks for <em>captive dependencies</em> at build
    /// time: a <see cref="Lifetime.Singleton"/> service that depends — directly or transitively,
    /// through its constructor parameters — on a <see cref="Lifetime.Scoped"/> service. Because the
    /// singleton outlives every scope, it would capture and reuse a scoped instance forever, which
    /// is almost always a bug. Each offending singleton produces a <see cref="CaptiveDependencyException"/>;
    /// all of them are collected and thrown as a single <see cref="AggregateException"/>, using the same
    /// aggregation as <see cref="ValidateOnBuild"/>. This mirrors the semantics of
    /// <c>Microsoft.Extensions.DependencyInjection</c>'s <c>ValidateScopes</c>: a transient captured by a
    /// singleton is tolerated and is <strong>not</strong> reported. Open-generic registrations are skipped
    /// (consistent with <see cref="ValidateOnBuild"/>) since they have no concrete type arguments to inspect.
    /// This check is independent of <see cref="ValidateOnBuild"/> and runs whenever it is enabled; when both
    /// are set, registration validation runs first, then scope validation. Defaults to <c>false</c>.
    /// </summary>
    public bool ValidateScopes { get; init; }
}
