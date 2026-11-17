using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RailNarrator.App.ViewModels;

/// <summary>Minimal observable base. A framework is not needed for this many bindings.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A command that runs a synchronous or asynchronous handler and reports when it is busy.</summary>
/// <remarks>
/// A handler failure is routed to the optional <c>onError</c> callback rather than faulting an
/// unobserved task: a command that fails silently (the button simply re-enables) leaves the user
/// with no idea anything went wrong. The callback is the view model's status/notes reporting.
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task>? _async;
    private readonly Action? _sync;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public RelayCommand(Action execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _sync = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _async = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        if (_sync is not null)
        {
            try { _sync(); }
            catch (Exception ex) { Report(ex); }
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        _ = RunAsync();

        async Task RunAsync()
        {
            try { await _async!().ConfigureAwait(true); }
            catch (Exception ex) { Report(ex); }
            finally
            {
                _running = false;
                RaiseCanExecuteChanged();
            }
        }
    }

    private void Report(Exception ex)
    {
        if (_onError is { } onError) onError(ex);
        else throw ex;
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
