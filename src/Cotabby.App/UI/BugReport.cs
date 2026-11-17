using System.Runtime.InteropServices;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.App.UI.Settings.Panes;
using Cotabby.Core.Models;

namespace Cotabby.App.UI;

/// <summary>
/// Opens a new GitHub issue with the device details filled in, as upstream's Report Bug does, so
/// the reporter only writes what happened. Nothing personal goes in: no paths, endpoint address
/// or file name of a model the user supplied.
/// </summary>
internal static class BugReport
{
    public const string IssuesUrl = "https://github.com/Msde-7/cotabby-windows/issues/new";

    public static void Open(AppHost host) => Controls.Ui.OpenUrl(Url(host));

    public static string Url(AppHost host) => $"{IssuesUrl}?body={Uri.EscapeDataString(Body(host))}";

    private static string Body(AppHost host)
    {
        var settings = host.Settings;
        bool endpoint = settings.Engine == EngineKind.Endpoint;
        return string.Join("\n",
            "**What happened**",
            "",
            "",
            "**Steps to reproduce**",
            "",
            "",
            "**Environment**",
            $"- Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"- Cotabby: {Branding.Version}",
            $"- Engine: {(endpoint ? "OpenAI-compatible endpoint" : "Local model")}",
            $"- Model: {(endpoint ? EndpointModel(settings) : LocalModel(host))}");
    }

    private static string EndpointModel(AppSettings settings) =>
        settings.EndpointModel.Length > 0 ? settings.EndpointModel : "none selected";

    private static string LocalModel(AppHost host)
    {
        var model = host.Runtime.ActiveModel ?? ModelCatalog.FindById(host.EffectiveModelId);
        string state = host.Runtime.IsReady ? "loaded" : "not loaded";
        return model switch
        {
            null => "none",
            { IsCustom: true } => $"own GGUF file ({state})",
            _ => $"{model.DisplayName}, {model.FileName} ({state})",
        };
    }
}
