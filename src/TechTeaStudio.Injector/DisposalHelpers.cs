namespace TechTeaStudio.Injector;

/// <summary>
/// Utility helpers for releasing tracked instances. Centralizes the policy: when an instance
/// implements both <see cref="IDisposable"/> and <see cref="IAsyncDisposable"/>, async dispose
/// is preferred from async paths and sync dispose is preferred from sync paths.
/// </summary>
internal static class DisposalHelpers
{
    /// <summary>True if <paramref name="instance"/> implements either disposable contract.</summary>
    public static bool IsTrackable(object? instance)
        => instance is IDisposable or IAsyncDisposable;

    /// <summary>
    /// Synchronously dispose <paramref name="instance"/>. Prefers <see cref="IDisposable"/>; if the
    /// instance is async-disposable only, blocks on its <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// Exceptions are swallowed.
    /// </summary>
    public static void DisposeSync(object instance)
    {
        try
        {
            switch (instance)
            {
                case IDisposable d:
                    d.Dispose();
                    break;
                case IAsyncDisposable ad:
                    ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
            }
        }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Asynchronously dispose <paramref name="instance"/>. Prefers <see cref="IAsyncDisposable"/>;
    /// falls back to <see cref="IDisposable"/>. Exceptions are swallowed.
    /// </summary>
    public static async ValueTask DisposeAsyncSafe(object instance)
    {
        try
        {
            switch (instance)
            {
                case IAsyncDisposable ad:
                    await ad.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable d:
                    d.Dispose();
                    break;
            }
        }
        catch { /* swallow */ }
    }
}
