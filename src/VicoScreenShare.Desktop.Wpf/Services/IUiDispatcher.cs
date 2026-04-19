namespace VicoScreenShare.Desktop.App.Services;

using System;
using System.Threading.Tasks;

/// <summary>
/// UI-thread marshaling abstraction. View models receive this through
/// constructor injection and never touch <c>Dispatcher.CurrentDispatcher</c>
/// themselves — capturing the "current" dispatcher at construction time is
/// a well-known source of bugs when the constructor happens to land on a
/// thread-pool thread (where <c>CurrentDispatcher</c> fabricates a fresh,
/// unpumped dispatcher, and every <c>BeginInvoke</c> silently queues work
/// that never runs). Routing every cross-thread UI update through a single
/// injected instance makes the concern explicit, testable, and impossible
/// to accidentally bind to the wrong thread.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the current thread is the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Fire-and-forget marshal. Runs synchronously if already on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Awaitable marshal. Runs synchronously if already on the UI thread.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Awaitable marshal with a return value.</summary>
    Task<T> InvokeAsync<T>(Func<T> function);

    /// <summary>Awaitable marshal for async work that runs on the UI thread.</summary>
    Task InvokeAsync(Func<Task> function);
}
