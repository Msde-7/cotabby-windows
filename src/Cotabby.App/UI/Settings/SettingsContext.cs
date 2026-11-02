using Cotabby.App.Hosting;
using Cotabby.App.Settings;

namespace Cotabby.App.UI.Settings;

/// <summary>
/// What every settings row shares. Rows read through <see cref="OnRefresh"/> and write
/// through <see cref="Change"/>, which is ignored while a refresh is setting control
/// values so that a refresh can never write back.
/// </summary>
internal sealed class SettingsContext(AppHost host, Action<SettingsPane> navigate)
{
    private readonly List<Action> _refreshers = [];
    private bool _refreshing;

    public AppHost Host => host;

    /// <summary>The live settings. Never cache this, a reset replaces the instance.</summary>
    public AppSettings Settings => host.Settings;

    /// <summary>Raised when the settings window closes.</summary>
    public event Action? Closed;

    public void Navigate(SettingsPane pane) => navigate(pane);

    /// <summary>Runs <paramref name="refresh"/> now and again after every settings change.</summary>
    public void OnRefresh(Action refresh)
    {
        _refreshers.Add(refresh);
        Guarded(refresh);
    }

    public void Refresh() => Guarded(() =>
    {
        foreach (var refresh in _refreshers) refresh();
    });

    /// <summary>Mutates the settings, applies them to the running app and saves them.</summary>
    public void Change(Action<AppSettings> mutate)
    {
        if (!_refreshing) host.Update(mutate);
    }

    /// <summary>Adds a process name to a list unless it is already there in any case.</summary>
    public void AddProcess(Func<AppSettings, List<string>> apps, string processName)
    {
        if (!apps(host.Settings).Contains(processName, StringComparer.OrdinalIgnoreCase)) Change(s => apps(s).Add(processName));
    }

    public void Close() => Closed?.Invoke();

    private void Guarded(Action action)
    {
        bool wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            action();
        }
        finally
        {
            _refreshing = wasRefreshing;
        }
    }
}
