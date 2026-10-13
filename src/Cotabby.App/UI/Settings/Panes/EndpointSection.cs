using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;
using Cotabby.Inference.OpenAICompatible;

namespace Cotabby.App.UI.Settings.Panes;

/// <summary>
/// The rows for an OpenAI-compatible endpoint. The API key goes only to the credential
/// store. It is never written to the settings file or shown again after saving.
/// </summary>
internal sealed class EndpointSection
{
    private readonly Rows _rows;
    private readonly SettingsContext _context;
    private readonly OpenAICompatibleConnectionModel _connection;
    private readonly CancellationTokenSource _closed = new();

    private readonly StatusLine _status = new();
    private readonly Button _test;
    private readonly ComboBox _discovered = new ComboBox { MinWidth = 44, ToolTip = "Models found on the server" }.Styled("Combo");
    private readonly PasswordBox _key = new PasswordBox { Width = 240 }.Styled("PasswordInput");
    private readonly TextBlock _keyStatus = Ui.Text("", "Caption");

    public EndpointSection(Rows rows)
    {
        _rows = rows;
        _context = rows.Context;
        _connection = new OpenAICompatibleConnectionModel(_context.Host.EndpointClient);
        _test = Ui.Button("Test Connection", TestConnection, "AccentBtn");

        _context.Closed += _closed.Cancel;
        _connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OpenAICompatibleConnectionModel.State)) ShowConnection();
        };
        _discovered.SelectionChanged += (_, _) =>
        {
            if (_discovered.SelectedItem is string model && model != _context.Settings.EndpointModel)
            {
                _context.Change(s => s.EndpointModel = model);
            }
        };
        ShowConnection();
        ShowKeyStatus();
    }

    public SettingsCard[] Cards() =>
    [
        new SettingsCard("Connection", ServerUrl(), ApiKey()),
        new SettingsCard("Endpoint Model",
            Model(),
            _rows.Segmented("API Format",
                "Chat uses /v1/chat/completions (recommended). Text uses /v1/completions "
                + "for base models that continue a raw prompt.",
                [(true, "Chat Completions"), (false, "Text Completions")],
                s => s.EndpointUsesChatApi, (s, v) => s.EndpointUsesChatApi = v)
                .Tagged("endpoint chat completion text completion api mode request openai")),
    ];

    private SettingsRow ServerUrl()
    {
        var url = new TextBox { Tag = OpenAICompatibleEndpointConfiguration.DefaultBaseUrl }.Styled("Input");
        _rows.Bind(url, s => s.EndpointBaseUrl, (s, v) =>
        {
            s.EndpointBaseUrl = v.Trim();
            // Models listed by the previous server do not apply to the new one.
            _connection.Invalidate();
        });
        var useDefault = Ui.Button("Use Ollama Default", () => _context.Change(s =>
        {
            s.EndpointBaseUrl = OpenAICompatibleEndpointConfiguration.DefaultBaseUrl;
            _connection.Invalidate();
        }));
        var warning = Ui.Text("", "Caption").Themed(TextBlock.ForegroundProperty, "OrangeBrush");
        warning.Margin = new Thickness(0, 8, 0, 0);

        _context.OnRefresh(() =>
        {
            var settings = _context.Settings;
            useDefault.IsEnabled = settings.EndpointBaseUrl != OpenAICompatibleEndpointConfiguration.DefaultBaseUrl;
            warning.Text = Validate(out var configuration) ?? configuration?.PrivacyWarning ?? "";
            warning.Visibility = warning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        DockPanel.SetDock(_test, Dock.Right);
        _test.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(useDefault, Dock.Right);
        var content = new StackPanel
        {
            Children =
            {
                new DockPanel { Children = { _test, url } },
                new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { useDefault, _status } },
                warning,
            },
        };
        return _rows.Stacked("Server URL",
            "The OpenAI-compatible server address. Cotabby adds /v1 when the address has no path; "
            + "Ollama uses http://127.0.0.1:11434 by default.", content)
            .Tagged("endpoint url host ollama localhost openai v1 connect connection status lm studio vllm");
    }

    private SettingsRow ApiKey()
    {
        var save = Ui.Button("Save", () => StoreKey(() => _context.Host.EndpointCredentials.SaveApiKey(_key.Password)));
        var clear = Ui.Button("Clear", () => StoreKey(_context.Host.EndpointCredentials.DeleteApiKey));
        var content = new StackPanel { Children = { Ui.Across(_key, save, clear), _keyStatus } };
        _keyStatus.Margin = new Thickness(0, 8, 0, 0);
        return _rows.Stacked("API Key",
            "Optional bearer token, stored encrypted for your Windows account. Ollama does not require one.", content)
            .Tagged("endpoint token bearer credential authentication password");
    }

    private SettingsRow Model()
    {
        var model = new TextBox { Tag = "Enter model identifier", Width = 220 }.Styled("Input");
        _rows.Bind(model, s => s.EndpointModel, (s, v) => s.EndpointModel = v.Trim());
        _context.OnRefresh(SelectDiscovered);
        return _rows.Labeled("Model",
            "Choose a model returned by the server or enter its identifier manually.", Ui.Across(model, _discovered))
            .Tagged("endpoint identifier ollama lm studio vllm");
    }

    /// <summary>Returns the user-facing problem with the endpoint settings, or null when they are valid.</summary>
    private string? Validate(out OpenAICompatibleEndpointConfiguration? configuration)
    {
        var settings = _context.Settings;
        try
        {
            configuration = new OpenAICompatibleEndpointConfiguration(
                settings.EndpointBaseUrl,
                settings.EndpointModel,
                settings.EndpointUsesChatApi ? OpenAICompatibleApiMode.ChatCompletions : OpenAICompatibleApiMode.Completions);
            return null;
        }
        catch (OpenAICompatibleEndpointException ex)
        {
            configuration = null;
            return ex.Message;
        }
    }

    private async void TestConnection()
    {
        if (Validate(out var configuration) is { } problem)
        {
            _connection.SetFailure(problem);
            return;
        }
        await _connection.RefreshAsync(configuration!, ReadKey(), _closed.Token);

        var current = _context.Settings.EndpointModel;
        var preferred = OpenAICompatibleModelSelectionResolver.PreferredSelection(current, _connection.Models);
        if (preferred is not null && preferred != current) _context.Change(s => s.EndpointModel = preferred);
    }

    private void ShowConnection()
    {
        var state = _connection.State;
        _status.Set(state is OpenAICompatibleConnectionState.Ready, state.Summary);
        _test.IsEnabled = state is not OpenAICompatibleConnectionState.Connecting;
        _discovered.ItemsSource = _connection.Models.Select(model => model.Id).ToList();
        _discovered.IsEnabled = _connection.Models.Count > 0;
        SelectDiscovered();
    }

    private void SelectDiscovered() => _discovered.SelectedItem = _context.Settings.EndpointModel;

    private void StoreKey(Action store)
    {
        try
        {
            store();
            _key.Clear();
            ShowKeyStatus();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _keyStatus.Text = "Could not update the stored key. " + ex.Message;
        }
    }

    private void ShowKeyStatus() => _keyStatus.Text = ReadKey() is null ? "No API key is stored." : "An API key is stored.";

    /// <summary>The stored key, or null when there is none or it cannot be decrypted.</summary>
    private string? ReadKey()
    {
        try
        {
            return _context.Host.EndpointCredentials.ReadApiKey();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
