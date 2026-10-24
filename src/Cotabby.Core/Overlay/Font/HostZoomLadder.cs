using Cotabby.Core.Context;

namespace Cotabby.Core.Overlay.Font;

/// <summary>
/// The zoom factors a Chromium host paints at, so a size measured from its text can be snapped to
/// the exact size it paints: the size it reports times one of those factors. Mirrors the macOS
/// <c>HostZoomLadder</c>.
/// </summary>
/// <remarks>
/// Chromium and Electron report font sizes without their zoom. Chrome zooms through fixed
/// percentages and Electron through half zoom levels, each level a factor of 1.2. A measured size
/// within <see cref="Tolerance"/> of the reported size times a factor on the ladder is that product.
/// </remarks>
public static class HostZoomLadder
{
    public enum Kind
    {
        /// <summary>Chrome, Edge, Brave and the like: page zoom through Chrome's presets.</summary>
        ChromeBrowser,

        /// <summary>An Electron app: zoom levels of 1.2 each, in half steps.</summary>
        Electron,
    }

    public static readonly double[] ChromeFactors =
    [
        0.25, 1.0 / 3.0, 0.5, 2.0 / 3.0, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5,
    ];

    public static readonly double[] ElectronFactors =
        Enumerable.Range(0, 35).Select(i => Math.Pow(1.2, -8 + i * 0.5)).ToArray();

    /// <summary>How far a measurement may sit from a ladder size and still be it.</summary>
    public const double Tolerance = 0.01;

    public static IReadOnlyList<double> Factors(Kind kind) =>
        kind == Kind.ChromeBrowser ? ChromeFactors : ElectronFactors;

    /// <summary>The ladder a host paints on, or null when it is not a Chromium host.</summary>
    public static Kind? For(string? processName) =>
        BrowserAppDetector.IsChromiumBrowser(processName) ? Kind.ChromeBrowser
        : BrowserAppDetector.IsElectronEditor(processName) ? Kind.Electron
        : null;

    /// <summary>
    /// The reported size times the factor nearest the measured size, when the measurement lies
    /// within <see cref="Tolerance"/> of that product; null otherwise.
    /// </summary>
    public static double? SnappedSize(double measured, double reported, Kind kind)
    {
        if (!(measured > 0) || !(reported > 0) || !double.IsFinite(measured) || !double.IsFinite(reported)) return null;
        double ratio = measured / reported;
        double factor = Factors(kind).MinBy(f => Math.Abs(f - ratio));
        return Math.Abs(ratio / factor - 1) <= Tolerance ? reported * factor : null;
    }
}
