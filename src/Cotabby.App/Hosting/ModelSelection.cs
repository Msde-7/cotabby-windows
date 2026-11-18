using Cotabby.App.Settings;
using Cotabby.Core.Models;
using Cotabby.Inference;
using Cotabby.Win32.Power;

namespace Cotabby.App.Hosting;

/// <summary>
/// Which local model the settings and the power source call for. It only decides: the power
/// source, whether a model file is on disk and the catalog are given to it, and writing a
/// replaced model id back is left to the caller.
/// </summary>
public sealed class ModelSelection(
    AppSettings settings,
    bool onBattery,
    Func<CotabbyModel, bool> isCached,
    Func<string, CotabbyModel?> findById,
    CotabbyModel fallback)
{
    /// <summary>The selection for the current power source, the models folder and the built-in catalog.</summary>
    public static ModelSelection Current(AppSettings settings) =>
        new(settings, PowerStatus.IsOnBattery, ModelDownloader.IsCached, ModelCatalog.FindById, ModelCatalog.All[0]);

    /// <summary>The model the settings name outside power switching, or the first built-in one.</summary>
    public CotabbyModel ActiveModel =>
        (settings.ActiveModelId is { } id ? findById(id) : null) ?? fallback;

    /// <summary>The model id for the current power source, when that feature is on and the slot names one.</summary>
    public string? PowerSlotId
    {
        get
        {
            if (!settings.PowerModelSwitching) return null;
            var id = onBattery ? settings.BatteryModelId : settings.PluggedInModelId;
            return id.Length > 0 ? id : null;
        }
    }

    /// <summary>The model chosen for the current power source, when that feature is on and the file is present.</summary>
    public CotabbyModel? PowerSourceModel =>
        PowerSlotId is { } id && findById(id) is { } model && isCached(model) ? model : null;

    /// <summary>
    /// The model a pick by hand changes and the model lists mark: while power switching is on, the
    /// model chosen for the current power source when it names one, otherwise the active model.
    /// </summary>
    public string SelectedModelId =>
        PowerSlotId is { } slot && findById(slot) is not null ? slot : ActiveModel.Id;

    /// <summary>The model to load: the power source's when it is on disk, otherwise the active one.</summary>
    public CotabbyModel ModelToLoad => PowerSourceModel ?? ActiveModel;

    /// <summary>
    /// Records a model picked by hand. While power switching is on the pick goes into the current
    /// power source's slot, as upstream, because the switcher would otherwise put the old one back.
    /// </summary>
    public static void Pick(AppSettings settings, string id, bool onBattery)
    {
        if (!settings.PowerModelSwitching) settings.ActiveModelId = id;
        else if (onBattery) settings.BatteryModelId = id;
        else settings.PluggedInModelId = id;
    }
}
