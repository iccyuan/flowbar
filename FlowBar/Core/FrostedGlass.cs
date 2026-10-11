using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowBar.Core;

/// <summary>
/// Snapshot-based frosted glass. DWM acrylic refuses to render behind non-activating / layered
/// windows on current Windows 11 builds, so we grab what is on screen, blur it ourselves and paint
/// it as the background. Capture once right before a surface appears, then every glass element
/// picks the slice of the snapshot that lies under it (see <see cref="EnabledProperty"/>).
/// </summary>
public static class FrostedGlass
{
    public sealed class Snapshot
    {
        public RECT Area;          // screen rect the image covers, physical px
        public BitmapSource Image = null!;
        public double Scale;       // image px per screen px
    }

    public static Snapshot? Current { get; private set; }

    /// <summary>Captures and blurs <paramref name="area"/> (physical screen px) and makes it current.</summary>
    public static Snapshot? Capture(RECT area, double scale = 0.5, int radius = 8)
    {
        try
        {
            var snap = Grab(area, scale, radius);
            if (snap != null) Current = snap;
            return snap;
        }
        catch (Exception ex)
        {
            Log.Write("glass capture failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Captures a region around a screen point, clamped to that monitor.</summary>
    public static Snapshot? CaptureAround(int x, int y, int halfW, int above, int below)
    {
        var probe = new RECT { Left = x, Top = y, Right = x + 1, Bottom = y + 1 };
        var mi = Native.MonitorInfoFor(ref probe);
        var r = new RECT
        {
            Left = Math.Max(mi.rcMonitor.Left, x - halfW),
            Right = Math.Min(mi.rcMonitor.Right, x + halfW),
            Top = Math.Max(mi.rcMonitor.Top, y - above),
            Bottom = Math.Min(mi.rcMonitor.Bottom, y + below),
        };
        return Capture(r);
    }

    public static bool Covers(Snapshot? s, RECT target) =>
        s != null && target.Left >= s.Area.Left && target.Top >= s.Area.Top && target.Right <= s.Area.Right && target.Bottom <= s.Area.Bottom;

    /// <summary>Brush showing the blurred backdrop under <paramref name="target"/>, or null if uncovered.</summary>
    public static ImageBrush? BrushFor(RECT target) => BrushFor(Current, target);

    public static ImageBrush? BrushFor(Snapshot? s, RECT target)
    {
        if (!Covers(s, target)) return null;
        return new ImageBrush(s!.Image)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect((target.Left - s.Area.Left) * s.Scale, (target.Top - s.Area.Top) * s.Scale,
                               target.Width * s.Scale, target.Height * s.Scale),
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
        };
    }

    // ---- attached behaviour: FrostedGlass.Enabled="True" on a Border -------------------

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(FrostedGlass), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private sealed class State { public RECT Rect; public Snapshot? Snap; public EventHandler? OnLayout; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, State> States = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b || !(bool)e.NewValue) return;
        var st = States.GetOrCreateValue(b);
        st.OnLayout = (_, _) => Refresh(b);
        // Popups (menus, tooltips) raise Loaded before WPF has moved their HWND into place, so the
        // first PointToScreen is wrong. Keep following layout while the element is on screen; the
        // rect cache below makes the repeated calls free.
        b.Loaded += (_, _) =>
        {
            Refresh(b);
            b.LayoutUpdated -= st.OnLayout;
            b.LayoutUpdated += st.OnLayout;
            b.Dispatcher.BeginInvoke(() => Refresh(b), System.Windows.Threading.DispatcherPriority.Background);
        };
        b.Unloaded += (_, _) => { b.LayoutUpdated -= st.OnLayout; st.Rect = default; st.Snap = null; };
        b.SizeChanged += (_, _) => Refresh(b);
    }

    /// <summary>Re-maps the element's backdrop from the current snapshot. Safe to call any time.</summary>
    public static void Refresh(Border b)
    {
        try
        {
            if (PresentationSource.FromVisual(b) == null || b.ActualWidth <= 0 || b.ActualHeight <= 0) return;
            var p0 = b.PointToScreen(new Point(0, 0));
            var p1 = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
            var rect = new RECT
            {
                Left = (int)Math.Floor(p0.X), Top = (int)Math.Floor(p0.Y),
                Right = (int)Math.Ceiling(p1.X), Bottom = (int)Math.Ceiling(p1.Y),
            };
            var st = States.GetOrCreateValue(b);
            // Prefer the newest snapshot, but never drop a snapshot that still covers us for one that
            // doesn't (another surface, e.g. the tooltip, may have captured a small region meanwhile).
            var snap = Covers(Current, rect) ? Current : Covers(st.Snap, rect) ? st.Snap : null;
            if (rect.Same(st.Rect) && ReferenceEquals(st.Snap, snap) && (snap != null || b.Background != null)) return;
            st.Rect = rect;
            st.Snap = snap;
            var brush = BrushFor(snap, rect);
            if (brush != null) b.Background = brush;
            else
            {
                b.SetResourceReference(Border.BackgroundProperty, "MenuBg"); // opaque fallback, never see-through
                if (Current != null) Log.Write($"glass fallback: element {rect} not covered by snapshot {Current.Area}");
            }
        }
        catch (Exception ex) { Log.Write("glass refresh failed: " + ex.Message); }
    }

    // ---- capture + blur ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public uint color0; // first palette entry, unused for 32bpp
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;

    private static unsafe Snapshot? Grab(RECT area, double scale, int radius)
    {
        int w = area.Width, h = area.Height;
        if (w <= 0 || h <= 0) return null;
        int dw = Math.Max(1, (int)Math.Round(w * scale)), dh = Math.Max(1, (int)Math.Round(h * scale));

        IntPtr sdc = Native.GetDC(IntPtr.Zero);
        if (sdc == IntPtr.Zero) return null;
        IntPtr mdc = IntPtr.Zero, hbm = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mdc = CreateCompatibleDC(sdc);
            var bmi = new BITMAPINFO { biSize = 40, biWidth = dw, biHeight = -dh, biPlanes = 1, biBitCount = 32 };
            hbm = CreateDIBSection(mdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hbm == IntPtr.Zero) return null;
            old = SelectObject(mdc, hbm);
            SetStretchBltMode(mdc, HALFTONE);
            if (!StretchBlt(mdc, 0, 0, dw, dh, sdc, area.Left, area.Top, w, h, SRCCOPY)) return null;
            GdiFlush();

            BoxBlur((byte*)bits, dw, dh, radius);

            var img = BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgr32, null, bits, dw * dh * 4, dw * 4);
            img.Freeze();
            return new Snapshot { Area = area, Image = img, Scale = (double)dw / w };
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mdc, old);
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (mdc != IntPtr.Zero) DeleteDC(mdc);
            Native.ReleaseDC(IntPtr.Zero, sdc);
        }
    }

    /// <summary>Three box-blur passes approximate a Gaussian well enough for a backdrop.</summary>
    private static unsafe void BoxBlur(byte* px, int w, int h, int r)
    {
        if (r < 1 || w < 2 || h < 2) return;
        var tmp = new byte[w * h * 4];
        fixed (byte* t = tmp)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                BlurLine(px, t, w, h, r, horizontal: true);
                BlurLine(t, px, w, h, r, horizontal: false);
            }
        }
    }

    private static unsafe void BlurLine(byte* src, byte* dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w;
        int len = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4;
        int lineStride = horizontal ? w * 4 : 4;
        int div = 2 * r + 1;
        for (int l = 0; l < lines; l++)
        {
            byte* s = src + l * lineStride;
            byte* d = dst + l * lineStride;
            int sb = 0, sg = 0, sr = 0;
            for (int i = -r; i <= r; i++)
            {
                byte* p = s + Math.Clamp(i, 0, len - 1) * step;
                sb += p[0]; sg += p[1]; sr += p[2];
            }
            for (int x = 0; x < len; x++)
            {
                byte* o = d + x * step;
                o[0] = (byte)(sb / div); o[1] = (byte)(sg / div); o[2] = (byte)(sr / div); o[3] = 255;
                byte* pOut = s + Math.Clamp(x - r, 0, len - 1) * step;
                byte* pIn = s + Math.Clamp(x + r + 1, 0, len - 1) * step;
                sb += pIn[0] - pOut[0]; sg += pIn[1] - pOut[1]; sr += pIn[2] - pOut[2];
            }
        }
    }
}
