using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Win32.Interop;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using OcrLine = Cotabby.Core.Context.OcrLine;
using static Cotabby.Win32.Interop.NativeMethods;
using static Cotabby.Win32.Interop.ScreenCaptureNative;

namespace Cotabby.Win32.Visual;

/// <summary>
/// Copies the focused window off the screen and reads it with the Windows OCR engine. The pixels
/// stay in memory and are dropped once read. Cotabby's own windows are excluded from capture, so
/// a suggestion on screen is never read back into the prompt.
/// </summary>
public sealed class WindowsScreenTextReader : IScreenTextReader
{
    // Upstream's crop for the endpoint, in device-independent pixels.
    private const int CropHorizontalPaddingDip = 160;
    private const int CropContextHeightDip = 800;
    private const int CacheLimit = 4;

    private readonly ILogger _logger;
    private readonly OcrEngine? _engine;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly List<(ulong Hash, int MaxChars, IReadOnlyList<OcrLine> Lines)> _cache = [];

    // Reused by every capture under _oneAtATime, so a large window is not a new large-object
    // allocation every few seconds. Zeroed once each read is done.
    private byte[] _pixels = [];

    public WindowsScreenTextReader(ILogger logger)
    {
        _logger = logger;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Windows OCR engine could not start.");
        }
        if (_engine is null) _logger.LogInformation("Screen context is unavailable: no OCR language is installed.");
    }

    /// <summary>False when Windows has no OCR language installed for the user's languages.</summary>
    public bool IsAvailable => _engine is not null;

    public async Task<ScreenCapture?> ReadAsync(
        FocusedField field, VisualContextConfiguration configuration, CancellationToken ct)
    {
        if (_engine is null) return null;
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Capture(field, configuration) is not { } image) return null;
            ct.ThrowIfCancellationRequested();

            ulong hash = PixelHash(image.Pixels, image.Width, image.Height);
            var lines = Cached(hash, configuration.MaxRecognizedCharacters);
            if (lines is null)
            {
                lines = await RecognizeAsync(image, configuration.MaxRecognizedCharacters).ConfigureAwait(false);
                Store(hash, configuration.MaxRecognizedCharacters, lines);
            }
            return new ScreenCapture(lines, image.FocusBounds, image.WindowTitle);
        }
        finally
        {
            Array.Clear(_pixels);
            _oneAtATime.Release();
        }
    }

    private sealed record CapturedImage(byte[] Pixels, int Width, int Height, OcrBounds? FocusBounds, string WindowTitle);

    /// <summary>Copies the window holding the field, or the area above the field for the endpoint.</summary>
    private CapturedImage? Capture(FocusedField field, VisualContextConfiguration configuration)
    {
        IntPtr hwnd = GetAncestor(NativeMethods.GetForegroundWindow(), GA_ROOT);
        if (hwnd == IntPtr.Zero || IsIconic(hwnd) || WindowBounds(hwnd) is not { } window) return null;

        // The field must be in this window. A packaged app's frame belongs to another process, so
        // the field's position is the test rather than the process id.
        var fieldRect = field.FieldRect.IsEmpty ? field.CaretRect : field.FieldRect;
        double cx = fieldRect.X + fieldRect.Width / 2, cy = fieldRect.Y + fieldRect.Height / 2;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        bool contains = cx >= window.Left && cx < window.Right && cy >= window.Top && cy < window.Bottom;
        if (pid != field.ProcessId && !contains) return null;

        var source = configuration.CapturesEntireWindow ? window : CropAbove(fieldRect, window, hwnd, configuration);
        source = ClipToVirtualScreen(source);
        if (source.Width < 16 || source.Height < 16) return null;

        double scale = Math.Min(1.0, (double)configuration.MaxImageDimension / Math.Max(source.Width, source.Height));
        scale = Math.Min(scale, (double)OcrEngine.MaxImageDimension / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        byte[]? pixels = CopyScreen(source, width, height);
        if (pixels is null) return null;

        // The field's place in the capture lets nearby lines outrank distant chrome.
        var caret = field.CaretRect.IsEmpty ? fieldRect : field.CaretRect;
        OcrBounds? focus = fieldRect.IsEmpty
            ? null
            : new OcrBounds(
                (fieldRect.X - source.Left) / source.Width,
                (caret.Y - source.Top) / source.Height,
                fieldRect.Width / source.Width,
                Math.Max(caret.Height, 1) / source.Height);
        return new CapturedImage(pixels, width, height, focus, WindowTitle(hwnd));
    }

    private static RECT? WindowBounds(IntPtr hwnd)
    {
        // The extended frame leaves out the invisible resize border of Windows 10 and 11.
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            && frame.Width > 0 && frame.Height > 0)
        {
            return frame;
        }
        return GetWindowRect(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    private static RECT CropAbove(ScreenRect field, RECT window, IntPtr hwnd, VisualContextConfiguration configuration)
    {
        double dpiScale = Math.Max(1, MonitorDpi(hwnd)) / 96.0;
        int padding = (int)(CropHorizontalPaddingDip * dpiScale);
        int height = Math.Min((int)(CropContextHeightDip * dpiScale), window.Height);
        int width = field.IsEmpty
            ? Math.Min((int)(configuration.SnapshotDimension * dpiScale) + padding * 2, window.Width)
            : Math.Min((int)field.Width + padding * 2, window.Width);
        int x = field.IsEmpty ? (int)(field.X - width / 2.0) : (int)field.X - padding;
        int y = (int)field.Y - height;
        x = Math.Clamp(x, window.Left, Math.Max(window.Left, window.Right - width));
        y = Math.Clamp(y, window.Top, Math.Max(window.Top, window.Bottom - height));
        return new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    /// <summary>
    /// The effective DPI of the monitor the window is on. A window that is not DPI aware reports 96
    /// for itself while its coordinates here are physical pixels, so its own DPI would make the crop
    /// too small on a scaled display.
    /// </summary>
    private static uint MonitorDpi(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0) return dpi;
        return GetDpiForWindow(hwnd);
    }

    private static RECT ClipToVirtualScreen(RECT rect)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new RECT
        {
            Left = Math.Max(rect.Left, left),
            Top = Math.Max(rect.Top, top),
            Right = Math.Min(rect.Right, right),
            Bottom = Math.Min(rect.Bottom, bottom),
        };
    }

    /// <summary>Copies a screen rectangle into a top-down 32-bit buffer of the given size.</summary>
    private unsafe byte[]? CopyScreen(RECT source, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            bitmap = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            previous = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, HALFTONE);
            // HALFTONE needs the brush origin set after it, or its dither pattern is misaligned.
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memory, 0, 0, width, height, screen, source.Left, source.Top, source.Width, source.Height, SRCCOPY))
            {
                return null;
            }
            // GDI may still be drawing into the section, so the batch is flushed before its bits are read.
            GdiFlush();
            if (_pixels.Length != width * height * 4) _pixels = new byte[width * height * 4];
            // GDI leaves alpha undefined. OCR reads premultiplied pixels, so they are made opaque.
            CopyOpaque(new ReadOnlySpan<uint>((void*)bits, width * height), MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan()));
            return _pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Copies BGRA pixels with every alpha byte set to 255.</summary>
    private static void CopyOpaque(ReadOnlySpan<uint> source, Span<uint> target)
    {
        const uint OpaqueAlpha = 0xFF000000;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var alpha = new Vector<uint>(OpaqueAlpha);
            for (; i <= source.Length - Vector<uint>.Count; i += Vector<uint>.Count)
            {
                (new Vector<uint>(source[i..]) | alpha).CopyTo(target[i..]);
            }
        }
        for (; i < source.Length; i++) target[i] = source[i] | OpaqueAlpha;
    }

    private async Task<IReadOnlyList<OcrLine>> RecognizeAsync(CapturedImage image, int maxCharacters)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine!.RecognizeAsync(bitmap);
        var lines = new List<OcrLine>();
        int characters = 0;
        foreach (var line in result.Lines)
        {
            string text = line.Text.Trim();
            if (text.Length == 0) continue;
            if (characters + text.Length > maxCharacters) break;
            characters += text.Length + 1;
            lines.Add(new OcrLine(text, 1f, LineBounds(line, image.Width, image.Height)));
        }
        return lines;
    }

    private static OcrBounds? LineBounds(Windows.Media.Ocr.OcrLine line, int width, int height)
    {
        if (line.Words.Count == 0) return null;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }
        return new OcrBounds(left / width, top / height, (right - left) / width, (bottom - top) / height);
    }

    /// <summary>
    /// FNV-1a over every 17th byte plus the size. A stride coprime with the pixel size touches every
    /// channel, so a color-only change still moves the hash. Identical pixels skip recognition.
    /// </summary>
    private static ulong PixelHash(byte[] pixels, int width, int height)
    {
        const ulong prime = 0x100000001B3;
        ulong hash = 0xcbf29ce484222325;
        for (int i = 0; i < pixels.Length; i += 17) hash = (hash ^ pixels[i]) * prime;
        hash = (hash ^ (ulong)width) * prime;
        return (hash ^ (ulong)height) * prime;
    }

    private IReadOnlyList<OcrLine>? Cached(ulong hash, int maxChars) =>
        _cache.FirstOrDefault(c => c.Hash == hash && c.MaxChars == maxChars).Lines;

    private void Store(ulong hash, int maxChars, IReadOnlyList<OcrLine> lines)
    {
        _cache.RemoveAll(c => c.Hash == hash);
        _cache.Add((hash, maxChars, lines));
        if (_cache.Count > CacheLimit) _cache.RemoveAt(0);
    }
}
