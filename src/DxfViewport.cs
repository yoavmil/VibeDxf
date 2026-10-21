using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontWeight = Vortice.DirectWrite.FontWeight;
using Rect = System.Windows.Rect;
using Size = Vortice.Mathematics.Size;
using DashStyle = Vortice.Direct2D1.DashStyle;
using FontStretch = Vortice.DirectWrite.FontStretch;
using ArcSegment = Vortice.Direct2D1.ArcSegment;
using SweepDirection = Vortice.Direct2D1.SweepDirection;
namespace VibeDxf;

/// <summary>
/// Pan, zoom, midpoint snapping and two-target measurement over a <see cref="DrawingRenderer"/>.
/// The swap chain lives in a child HWND that is transparent to the mouse, so input arrives on this WPF element.
/// </summary>
public sealed class DxfViewport : System.Windows.Controls.Border
{
    private static readonly Color4 BackColor = new(17 / 255f, 24 / 255f, 32 / 255f, 1);
    private static readonly Color4 LineColor = new(211 / 255f, 211 / 255f, 211 / 255f, 1);
    private static readonly Color4 Orange = new(1, 165 / 255f, 0, 1);
    private static readonly Color4 Blue = new(0, 191 / 255f, 1, 1);
    private static readonly Color4 Green = new(0, 250 / 255f, 154 / 255f, 1);
    private static readonly Color4 Snap = new(154 / 255f, 205 / 255f, 50 / 255f, 1);
    private static readonly Color4 Label = new(17 / 255f, 24 / 255f, 32 / 255f, 230 / 255f);
    private static readonly Color4 Hint = new(119 / 255f, 136 / 255f, 153 / 255f, 1);
    private readonly SwapChainHost host;
    private double scale = 1;
    private System.Windows.Vector offset;
    private Point? panStart;
    private Rect bounds = Rect.Empty;
    private bool renderQueued;
    private MidpointIndex? midpointIndex;
    private MeasurementTarget? hoveredMidpoint;
    private readonly List<MeasurementTarget> selected = [];
    public DxfDocument? Document { get; private set; }
    private int activeLayout = -1;
    /// <summary>
    /// −1 shows model space; otherwise the index into <see cref="DxfDocument.Layouts"/>. Switching only changes the
    /// camera, picking and draw ranges; GPU geometry is uploaded once per document.
    /// </summary>
    public int ActiveLayout
    {
        get => activeLayout;
        set
        {
            if (Document == null || value == activeLayout || value < -1 || value >= Document.Layouts.Count) return;
            activeLayout = value; ApplyLayout();
        }
    }
    public event Action? LayoutChanged;
    private DxfLayout? Layout => Document != null && activeLayout >= 0 ? Document.Layouts[activeLayout] : null;
    // Layout picking covers that layout's paper-space entities only; model content inside viewports is display-only.
    private IReadOnlyList<DxfEntity> ActiveEntities => Layout?.Entities ?? Document?.Entities ?? [];
    public IReadOnlyList<MeasurementTarget> Selected => selected;
    public ClosestPair? Measurement { get; private set; }
    private MeasurementStrategy firstStrategy, secondStrategy;
    public MeasurementStrategy FirstStrategy
    {
        get => firstStrategy;
        set { if (firstStrategy == value) return; firstStrategy = value; UpdateMeasurement(); }
    }
    public MeasurementStrategy SecondStrategy
    {
        get => secondStrategy;
        set { if (secondStrategy == value) return; secondStrategy = value; UpdateMeasurement(); }
    }
    public event Action? SelectionChanged;

    private void UpdateMeasurement()
    {
        Measurement = selected.Count == 2 ? selected[0].Distance(selected[1], FirstStrategy, SecondStrategy) : null;
        Invalidate(); SelectionChanged?.Invoke();
    }

    public DxfViewport()
    {
        host = new SwapChainHost(this);
        Child = host;
        Background = Brushes.Transparent;
        Focusable = true;
        Cursor = Cursors.Cross;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0 && Document != null) Fit(); else Invalidate(); };
    }
    public void SetDocument(DxfDocument doc)
    {
        Document = doc;
        activeLayout = doc.Entities.Count == 0 && doc.Layouts.Count > 0 ? 0 : -1;
        Upload();
        ApplyLayout();
    }
    private void ApplyLayout()
    {
        bounds = Layout?.Bounds ?? Document!.Bounds;
        midpointIndex = new MidpointIndex(ActiveEntities);
        ClearSelection(); Fit(); LayoutChanged?.Invoke();
    }
    private void Upload()
    {
        if (Document is { } doc)
            host.Renderer?.SetDrawing(doc.Entities, Center(doc.Bounds),
                [.. doc.Layouts.Select(l => ((IEnumerable<DxfEntity>)l.Entities, Center(l.Bounds)))]);
    }
    private static Point Center(Rect r) => r.IsEmpty ? default : new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
    internal void RendererCreated()
    {
        Upload();
        Invalidate();
    }
    public void Fit()
    {
        if (Document == null || ActualWidth < 1 || ActualHeight < 1) return;
        var b = bounds;
        scale = Math.Clamp(Math.Min(Math.Max(1, ActualWidth - 70) / Math.Max(b.Width, 1e-6),
            Math.Max(1, ActualHeight - 70) / Math.Max(b.Height, 1e-6)), 1e-9, 1e9);
        offset = new System.Windows.Vector(ActualWidth / 2 - (b.Left + b.Width / 2) * scale,
            ActualHeight / 2 + (b.Top + b.Height / 2) * scale);
        hoveredMidpoint = null; Invalidate();
    }
    public void ClearSelection()
    {
        selected.Clear(); Measurement = null; hoveredMidpoint = null;
        Invalidate(); SelectionChanged?.Invoke();
    }
    private Point World(Point p) => new((p.X - offset.X) / scale, (offset.Y - p.Y) / scale);
    private Point Screen(Point p) => new(p.X * scale + offset.X, -p.Y * scale + offset.Y);
    private Vector2 ScreenV(Point p) { var s = Screen(p); return new((float)s.X, (float)s.Y); }

    // Coalesces any number of changes into one frame per dispatcher pass.
    internal void Invalidate()
    {
        if (renderQueued) return;
        renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderNow);
    }
    private void RenderNow()
    {
        renderQueued = false;
        if (host.Renderer is not { } renderer) return;
        var (w, h) = host.PixelSize;
        renderer.Render(w, h, VisualTreeHelper.GetDpi(this).DpiScaleX, scale, offset, BackColor, LineColor, DrawOverlay,
            activeLayout, Layout?.Viewports);
    }

    private void DrawOverlay(ID2D1DeviceContext dc)
    {
        if (Document == null)
        {
            DrawText(dc, "Open or drop a DXF drawing\n\nCtrl+O  ·  Wheel to zoom  ·  Middle/right drag to pan", 20, Hint,
                null, w => new(Math.Max(20, (ActualWidth - w) / 2), Math.Max(20, ActualHeight / 2 - 50)));
            return;
        }
        for (int i = 0; i < selected.Count; i++)
        {
            var color = i == 0 ? Orange : Blue;
            if (selected[i].Midpoint is Point point) DrawMidpointMarker(dc, ScreenV(point), color);
            else
            {
                var entity=selected[i].Entity;
                var highlight=entity.Kind=="HATCH" && entity.RenderCurves is { } strokes
                    ? entity.Curves.Concat(strokes) : entity.Curves;
                using var geometry = BuildScreenGeometry(highlight);
                using var brush = dc.CreateSolidColorBrush(color);
                dc.DrawGeometry(geometry, brush, 3);
                if(entity.Kind=="HATCH")
                    foreach(var dot in entity.RenderDots)dc.FillEllipse(new Ellipse(ScreenV(dot),2,2),brush);
                if (selected[i].FixedPoint(i == 0 ? FirstStrategy : SecondStrategy) is Point center)
                {
                    var p = ScreenV(center);
                    dc.DrawLine(p - new Vector2(6, 0), p + new Vector2(6, 0), brush, 2);
                    dc.DrawLine(p - new Vector2(0, 6), p + new Vector2(0, 6), brush, 2);
                }
            }
        }
        if (Measurement is { } m)
        {
            var a = ScreenV(m.A); var b = ScreenV(m.B);
            using var brush = dc.CreateSolidColorBrush(Green);
            using var dash = host.Renderer!.Factory2D.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Dash });
            dc.DrawLine(a, b, brush, 1.5f, dash);
            dc.FillEllipse(new Ellipse(a, 4, 4), brush);
            dc.FillEllipse(new Ellipse(b, 4, 4), brush);
            DrawText(dc, (selected.Any(t => t.Entity.Kind == "SPLINE" || t.Entity.IsApproximate) ? "≈ " : "") + m.Distance.ToString("0.######"), 14, Green, FontWeight.SemiBold, (w, h) =>
                new(Math.Clamp((a.X + b.X) / 2 + 10, 0, Math.Max(0, ActualWidth - w - 8)),
                    Math.Clamp((a.Y + b.Y) / 2 - 24, 0, Math.Max(0, ActualHeight - h - 8))), 4, 2);
        }
        if (hoveredMidpoint?.Midpoint is Point snap)
        {
            var s = ScreenV(snap);
            DrawMidpointMarker(dc, s, Snap);
            DrawText(dc, "Midpoint", 12, Snap, null, (w, h) =>
                new(Math.Clamp(s.X + 12, 2, Math.Max(2, ActualWidth - w - 6)),
                    Math.Clamp(s.Y - h - 8, 2, Math.Max(2, ActualHeight - h - 6))), 2, 2);
        }
    }
    private void DrawText(ID2D1DeviceContext dc, string text, float size, Color4 color, FontWeight? weight,
        Func<double, Point> place) => DrawText(dc, text, size, color, weight, (w, _) => place(w), -1, 0);
    private void DrawText(ID2D1DeviceContext dc, string text, float size, Color4 color, FontWeight? weight,
        Func<double, double, Point> place, float padX, float padY)
    {
        var write = host.Renderer!.Write;
        using var format = write.CreateTextFormat("Segoe UI", weight ?? FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
        using var layout = write.CreateTextLayout(text, format, 4096, 4096);
        var metrics = layout.Metrics;
        var at = place(metrics.Width, metrics.Height);
        if (padX >= 0)
        {
            using var back = dc.CreateSolidColorBrush(Label);
            dc.FillRectangle(new Vortice.RawRectF((float)at.X - padX, (float)at.Y - padY,
                (float)at.X + metrics.Width + padX, (float)at.Y + metrics.Height + padY), back);
        }
        using var brush = dc.CreateSolidColorBrush(color);
        dc.DrawTextLayout(new Vector2((float)at.X, (float)at.Y), layout, brush, DrawTextOptions.None);
    }
    private void DrawMidpointMarker(ID2D1DeviceContext dc, Vector2 p, Color4 color)
    {
        using var triangle = host.Renderer!.Factory2D.CreatePathGeometry();
        using (var sink = triangle.Open())
        {
            sink.BeginFigure(new Vector2(p.X, p.Y - 7), FigureBegin.Filled);
            sink.AddLine(new Vector2(p.X - 7, p.Y + 5));
            sink.AddLine(new Vector2(p.X + 7, p.Y + 5));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        using var fill = dc.CreateSolidColorBrush(BackColor);
        using var brush = dc.CreateSolidColorBrush(color);
        dc.FillGeometry(triangle, fill);
        dc.DrawGeometry(triangle, brush, 2);
        dc.FillEllipse(new Ellipse(p, 1.5f, 1.5f), brush);
    }
    // Selected entities are few, so their outline is rebuilt in screen space every frame for a constant 3 px stroke.
    private ID2D1PathGeometry BuildScreenGeometry(IEnumerable<Curve> curves)
    {
        var geometry = host.Renderer!.Factory2D.CreatePathGeometry();
        using var sink = geometry.Open();
        foreach (var c in curves)
        {
            if (c.IsUnbounded)
            {
                if (ClipToView(c) is (Vector2 a, Vector2 b))
                {
                    sink.BeginFigure(a, FigureBegin.Hollow); sink.AddLine(b); sink.EndFigure(FigureEnd.Open);
                }
                continue;
            }
            sink.BeginFigure(ScreenV(c.Start), FigureBegin.Hollow);
            if (c.IsLine) sink.AddLine(ScreenV(c.End));
            else
            {
                int count = Math.Abs(c.Sweep) >= Math.Tau - 1e-10 ? 2 : 1;
                var r = (float)(c.Radius * scale);
                for (int i = 1; i <= count; i++)
                    sink.AddArc(new ArcSegment(ScreenV(c.At((double)i / count)), new Size(r, r), 0,
                        // ScreenV flips Y, so positive DXF sweeps remain visually counterclockwise.
                        c.Sweep > 0 ? SweepDirection.CounterClockwise : SweepDirection.Clockwise,
                        Math.Abs(c.Sweep / count) > Math.PI ? ArcSize.Large : ArcSize.Small));
            }
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geometry;
    }
    // Clips a ray or xline to the viewport (with a small margin) in screen space, keeping Direct2D coordinates small.
    private (Vector2, Vector2)? ClipToView(Curve c)
    {
        var p = Screen(c.Start); var d = Screen(c.End) - p;
        double lo = c.TMin, hi = c.TMax, margin = 10;
        bool Slab(double origin, double delta, double min, double max)
        {
            if (Math.Abs(delta) < 1e-300) return origin >= min && origin <= max;
            var t0 = (min - origin) / delta; var t1 = (max - origin) / delta;
            if (t0 > t1) (t0, t1) = (t1, t0);
            lo = Math.Max(lo, t0); hi = Math.Min(hi, t1);
            return lo <= hi;
        }
        if (!Slab(p.X, d.X, -margin, ActualWidth + margin) || !Slab(p.Y, d.Y, -margin, ActualHeight + margin)) return null;
        var a = p + d * lo; var b = p + d * hi;
        return (new Vector2((float)a.X, (float)a.Y), new Vector2((float)b.X, (float)b.Y));
    }

    private void SetHoveredMidpoint(MeasurementTarget? target)
    {
        if (Equals(hoveredMidpoint, target)) return;
        hoveredMidpoint = target; Invalidate();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { SetHoveredMidpoint(null); base.OnMouseLeave(e); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Document == null) return;
        var at = e.GetPosition(this); var world = World(at);
        scale = Math.Clamp(scale * Math.Pow(1.18, e.Delta / 120.0), 1e-9, 1e9);
        offset = new System.Windows.Vector(at.X - world.X * scale, at.Y + world.Y * scale);
        hoveredMidpoint = null; Invalidate(); e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            SetHoveredMidpoint(null);
            panStart = e.GetPosition(this); CaptureMouse(); Cursor = Cursors.Hand; e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left || Document == null) return;
        SelectAt(World(e.GetPosition(this)), !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }
    private void SelectAt(Point point, bool snapMidpoint)
    {
        if (Document == null) return;
        var target = snapMidpoint ? midpointIndex?.Find(point, 8 / scale) : null;
        if (target == null)
        {
            double nearest = 7 / scale;
            foreach (var entity in ActiveEntities)
            {
                if (entity.IsAnnotation) continue;
                var b = entity.Bounds; b.Inflate(nearest, nearest);
                // Ray and xline bounds hold only their base point, so they skip the box prefilter.
                if (!entity.IsUnbounded && !b.Contains(point)) continue;
                var distance = (entity.Closest(point)-point).Length;
                if (distance < nearest) { target = new MeasurementTarget(entity); nearest = distance; }
            }
        }
        if (target == null) { ClearSelection(); return; }
        if (!selected.Remove(target))
        {
            if (selected.Count == 2) selected.Clear();
            selected.Add(target);
        }
        hoveredMidpoint = null;
        UpdateMeasurement();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var next = e.GetPosition(this);
        if (panStart is Point start) { offset += next - start; panStart = next; Invalidate(); }
        else SetHoveredMidpoint(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? null : midpointIndex?.Find(World(next), 8 / scale));
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        { panStart = null; ReleaseMouseCapture(); Cursor = Cursors.Cross; }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        panStart = null; Cursor = Cursors.Cross; base.OnLostMouseCapture(e);
    }
}

/// <summary>
/// Child window that owns the swap chain. It returns HTTRANSPARENT, so Windows routes all mouse input
/// to the WPF parent window and the viewport handles it like any WPF element.
/// </summary>
internal sealed class SwapChainHost(DxfViewport owner) : HwndHost
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WM_NCHITTEST = 0x0084, WM_ERASEBKGND = 0x0014, WM_PAINT = 0x000F, WM_SIZE = 0x0005, HTTRANSPARENT = -1;
    private IntPtr hwnd;
    public DrawingRenderer? Renderer { get; private set; }
    public (int Width, int Height) PixelSize
    {
        get { GetClientRect(hwnd, out var r); return (r.Right - r.Left, r.Bottom - r.Top); }
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        CreateRenderer();
        // Closing the window only parks this HWND (WPF never calls DestroyWindowCore), so tie the GPU to the visual tree.
        Unloaded += (_, _) => { Renderer?.Dispose(); Renderer = null; };
        Loaded += (_, _) => { if (Renderer == null) CreateRenderer(); };
        return new HandleRef(this, hwnd);
    }
    private void CreateRenderer()
    {
        var (w, h) = PixelSize;
        Renderer = new DrawingRenderer(hwnd, w, h);
        owner.RendererCreated();
    }
    protected override void DestroyWindowCore(HandleRef window)
    {
        Renderer?.Dispose(); Renderer = null;
        DestroyWindow(window.Handle);
    }
    protected override IntPtr WndProc(IntPtr window, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST: handled = true; return HTTRANSPARENT;
            case WM_ERASEBKGND: handled = true; return 1;
            case WM_PAINT: ValidateRect(window, 0); owner.Invalidate(); handled = true; return 0;
            case WM_SIZE: owner.Invalidate(); break;
        }
        return base.WndProc(window, msg, wParam, lParam, ref handled);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
}
