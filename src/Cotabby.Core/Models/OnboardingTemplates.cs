namespace Cotabby.Core.Models;

/// <summary>The starting points the first-run window offers, in display order. Mirrors upstream's curated tiers.</summary>
public enum OnboardingTemplate
{
    Quick,
    Everyday,
    Powerful,
}

/// <summary>
/// What a starting point sets up: the local model and the behavior flags. Upstream turns these into
/// settings the same way, with multi-line off for every tier so a new user never gets block
/// completions before choosing them.
/// </summary>
public sealed record OnboardingTemplatePlan(
    OnboardingTemplate Template,
    CotabbyModel Model,
    int LowWords,
    int HighWords,
    bool AllowMultiLine,
    bool ClipboardContext);

/// <summary>
/// Whether a starting point is recommended on this PC, cannot be picked, or deserves a warning.
/// A disabled one cannot run its model at all. A warning only says it may be slow.
/// </summary>
public sealed record OnboardingTemplateAvailability(
    OnboardingTemplate Template,
    bool IsRecommended,
    bool IsDisabled,
    string? Warning);

/// <summary>
/// Port of upstream's OnboardingTemplateRecommender. Pure rules over the installed memory, so the
/// first-run window only renders them. Every tier here runs a local model, so the memory rules
/// always apply, as they do for upstream's open source engine.
/// </summary>
public static class OnboardingTemplateRecommender
{
    /// <summary>Below this much memory the Powerful model leaves too little headroom, so it cannot be picked.</summary>
    public const double PowerfulDisableBelowGigabytes = 8;

    /// <summary>From the disable floor up to this, Powerful is allowed but may be slow when other apps need memory.</summary>
    public const double PowerfulWarnBelowGigabytes = 16;

    /// <summary>Below this Everyday may be slow, and Quick becomes the recommended starting point.</summary>
    public const double EverydayWarnBelowGigabytes = 8;

    /// <summary>The settings each starting point applies. The models are matched by GGUF file, as upstream names them.</summary>
    public static OnboardingTemplatePlan Plan(OnboardingTemplate template) => template switch
    {
        OnboardingTemplate.Quick => new(template, Model("Qwen3.5-0.8B-Base.i1-Q6_K.gguf"), 4, 7, false, false),
        OnboardingTemplate.Everyday => new(template, Model("gemma-4-E2B.i1-Q6_K.gguf"), 7, 12, false, true),
        OnboardingTemplate.Powerful => new(template, Model("gemma-4-E4B.i1-Q4_K_M.gguf"), 12, 20, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(template)),
    };

    /// <summary>
    /// The single starting point to highlight. Low-memory PCs stay on Quick and everyone else gets
    /// Everyday. Powerful is never the default, it is for users who want the large model.
    /// </summary>
    public static OnboardingTemplate Recommended(ulong physicalMemoryBytes) =>
        Gigabytes(physicalMemoryBytes) < EverydayWarnBelowGigabytes ? OnboardingTemplate.Quick : OnboardingTemplate.Everyday;

    public static OnboardingTemplateAvailability Availability(OnboardingTemplate template, ulong physicalMemoryBytes)
    {
        double gigabytes = Gigabytes(physicalMemoryBytes);
        string size = SizeLabel(Plan(template).Model);
        bool disabled = false;
        string? warning = null;
        switch (template)
        {
            case OnboardingTemplate.Everyday when gigabytes < EverydayWarnBelowGigabytes:
                warning = $"Uses a {size} model, which may run slowly on this PC.";
                break;
            case OnboardingTemplate.Powerful when gigabytes < PowerfulDisableBelowGigabytes:
                disabled = true;
                warning = $"Needs more memory than this PC has (uses a {size} model).";
                break;
            case OnboardingTemplate.Powerful when gigabytes < PowerfulWarnBelowGigabytes:
                warning = $"Uses a {size} model; may run slowly with less than 16 GB of memory.";
                break;
        }
        return new(template, template == Recommended(physicalMemoryBytes), disabled, warning);
    }

    /// <summary>Binary gigabytes, the unit Windows and macOS use for installed memory.</summary>
    public static double Gigabytes(ulong bytes) => bytes / 1_073_741_824.0;

    /// <summary>The download size as the model pickers show it, such as "3.8 GB".</summary>
    public static string SizeLabel(CotabbyModel model) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{model.ApproxSizeBytes / 1e9:0.0} GB");

    private static CotabbyModel Model(string fileName) =>
        ModelCatalog.All.Single(model => model.FileName == fileName);
}
