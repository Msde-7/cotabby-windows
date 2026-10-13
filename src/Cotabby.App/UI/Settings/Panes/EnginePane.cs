using System.IO;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Models;

namespace Cotabby.App.UI.Settings.Panes;

internal static class EnginePane
{
    /// <summary>An empty id means the power source does not change the model.</summary>
    private static (string Value, string Label)[] PowerModels =>
    [
        ("", "Keep current model"),
        .. ModelCatalog.Available().Select(model => (model.Id, model.DisplayName)),
    ];

    public static SettingsPane Build(Rows r)
    {
        var context = r.Context;
        var runtimeStatus = new StatusLine();
        context.OnRefresh(() =>
        {
            var runtime = context.Host.Runtime;
            runtimeStatus.Set(runtime.IsReady, runtime.IsReady ? $"Ready · {runtime.ActiveModel?.DisplayName}" : "Not loaded");
        });

        return new SettingsPane("Engine & Model", "\uE950", "Choose the engine and models",
        [
            new SettingsCard("Engine",
                r.Combo("Engine",
                    "Local model runs a downloaded model file on this PC. "
                    + "Endpoint connects to an OpenAI-compatible server you manage.",
                    [(EngineKind.Local, "Local model"), (EngineKind.Endpoint, "OpenAI-compatible endpoint")],
                    s => s.Engine, (s, v) => s.Engine = v)
                    .Tagged("backend provider runtime local ollama openai compatible lm studio vllm on-device")),
            new SettingsCard("Power",
                r.Toggle("Switch Based on Power Source",
                    "Use a different local model on battery and while plugged in. "
                    + "For example, a small model on battery to save power and a larger one while charging.",
                    s => s.PowerModelSwitching, (s, v) => s.PowerModelSwitching = v)
                    .Tagged("battery plugged energy ac charger adaptive auto switch"),
                r.Combo("On Battery", "Local model to use while on battery.", PowerModels,
                    s => s.BatteryModelId, (s, v) => s.BatteryModelId = v)
                    .Tagged("unplugged low power energy saver model")
                    .VisibleWhen(s => s.PowerModelSwitching),
                r.Combo("Plugged In", "Local model to use while plugged in.", PowerModels,
                    s => s.PluggedInModelId, (s, v) => s.PluggedInModelId = v)
                    .Tagged("ac charger wall power wired model")
                    .VisibleWhen(s => s.PowerModelSwitching)),
            .. Local(
                new SettingsCard("Status",
                    r.Labeled("Model Status", "Whether the local model is loaded and ready.", runtimeStatus)
                        .Tagged("loaded ready runtime running health loading")),
                new SettingsCard("Models",
                    r.Stacked("Download Models",
                        "Curated models you can download and run. Click a downloaded model to switch to it.",
                        new ModelList(context))
                        .Tagged("selected active choose current default catalog fetch install get library")),
                new SettingsCard("Folder", ModelsFolder(r))),
            .. new EndpointSection(r).Cards().Select(card => card.VisibleWhen(s => s.Engine == EngineKind.Endpoint)),
        ]);
    }

    /// <summary>The row that shows the models folder. The Advanced pane lists it too.</summary>
    public static SettingsRow ModelsFolder(Rows r) =>
        r.Action("Models Folder", ModelCatalog.DefaultLocalDirectory(), "Open Folder", () =>
        {
            var folder = ModelCatalog.DefaultLocalDirectory();
            Directory.CreateDirectory(folder);
            Ui.OpenPath(folder);
        })
        .Tagged("path directory open reveal location storage downloaded files");

    private static IEnumerable<SettingsCard> Local(params SettingsCard[] cards) =>
        cards.Select(card => card.VisibleWhen(s => s.Engine == EngineKind.Local));
}
