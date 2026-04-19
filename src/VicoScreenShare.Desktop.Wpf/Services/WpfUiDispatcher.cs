namespace VicoScreenShare.Desktop.App.Services;

using System;
using System.Threading.Tasks;
using System.Windows.Threading;

/// <summary>
/// WPF implementation of <see cref="IUiDispatcher"/>. Wraps a single
/// <see cref="Dispatcher"/> instance — the UI thread's — captured once
/// at the composition root (see <see cref="NavigationService"/>'s ctor,
/// which is WPF-guaranteed to run on the UI thread because it's
/// instantiated from <c>MainWindow.xaml</c>'s DataContext).
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// The underlying WPF dispatcher. Exposed for the few APIs that only
    /// accept a <see cref="Dispatcher"/> directly (notably
    /// <see cref="DispatcherTimer"/>); regular VM code should prefer the
    /// <see cref="IUiDispatcher"/> methods on this class.
    /// </summary>
    public Dispatcher Dispatcher => _dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (action is null)
        {
            return;
        }
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.BeginInvoke(action);
    }

    public Task InvokeAsync(Action action)
    {
        if (action is null)
        {
            return Task.CompletedTask;
        }
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(action).Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (_dispatcher.CheckAccess())
        {
            return Task.FromResult(function());
        }
        return _dispatcher.InvokeAsync(function).Task;
    }

    public Task InvokeAsync(Func<Task> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (_dispatcher.CheckAccess())
        {
            return function();
        }
        return _dispatcher.InvokeAsync(function).Task.Unwrap();
    }
}
