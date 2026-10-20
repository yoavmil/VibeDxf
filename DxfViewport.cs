using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace CodexDXF;

public sealed class DxfViewport : FrameworkElement
{
    private StreamGeometry? drawing;
    private readonly ContainerVisual scene = new();
    private readonly DrawingVisual baseVisual = new();
    private readonly DrawingVisual selectionVisual = new();
    private readonly DrawingVisual measurementVisual = new();
    private readonly DrawingVisual snapVisual = new();
    private MidpointIndex? midpointIndex;
    private MeasurementTarget? hoveredMidpoint;
    private readonly MatrixTransform cameraTransform = new();
    private readonly VisualCollection visuals;
    private static readonly Brush BackgroundBrush = MakeBrush(Color.FromRgb(17, 24, 32));
    private static readonly Brush LabelBrush = MakeBrush(Color.FromArgb(230, 17, 24, 32));
    private static readonly Pen MeasurementPen = MakeMeasurementPen();

    protected override int VisualChildrenCount => visuals.Count;
    protected override Visual GetVisualChild(int index) => visuals[index];
    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private static Pen MakeMeasurementPen()
    {
        var pen = new Pen(Brushes.MediumSpringGreen, 1.5) { DashStyle = DashStyles.Dash };
        pen.Freeze(); return pen;
    }
    private double scale = 1;
    private Vector offset;
    private Point? panStart;
    private readonly List<MeasurementTarget> selected = [];
    private readonly Dictionary<MeasurementTarget, StreamGeometry> selectionGeometry = [];
    public DxfDocument? Document { get; private set; }
    public IReadOnlyList<MeasurementTarget> Selected => selected;
    public ClosestPair? Measurement { get; private set; }
    public event Action? SelectionChanged;
    public DxfViewport()
    {
        visuals = new VisualCollection(this) { scene, measurementVisual, snapVisual };
        scene.Children.Add(baseVisual);
        scene.Children.Add(selectionVisual);
        scene.Transform = cameraTransform;
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0 && Document != null) Fit(); else { DrawMeasurement(); InvalidateVisual(); } };
    }
    public void SetDocument(DxfDocument doc)
    {
        var geometry = Build(doc.Entities.SelectMany(e => e.Curves));
        Document = doc; drawing = geometry;
        midpointIndex = new MidpointIndex(doc.Entities);
        ClearSelection(); Fit();
        InvalidateVisual();
    }
    private static StreamGeometry Build(IEnumerable<Curve> curves)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open()) foreach (var c in curves)
        {
            ctx.BeginFigure(c.Start, false, false);
            if (c.IsLine) ctx.LineTo(c.End, true, false);
            else
            {
                // Split full circles into two arcs: WPF ignores coincident arc endpoints.
                int count = Math.Abs(c.Sweep) >= Math.Tau - 1e-10 ? 2 : 1;
                for (int i = 1; i <= count; i++)
                    ctx.ArcTo(c.At((double)i / count), new Size(c.Radius, c.Radius), 0,
                        Math.Abs(c.Sweep / count) > Math.PI,
                        c.Sweep > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
        }
        g.Freeze(); return g;
    }
    public void Fit()
    {
        if (Document == null || ActualWidth < 1 || ActualHeight < 1) return;
        var b = Document.Bounds;
        scale = Math.Clamp(Math.Min(Math.Max(1, ActualWidth - 70) / Math.Max(b.Width, 1e-6),
            Math.Max(1, ActualHeight - 70) / Math.Max(b.Height, 1e-6)), 1e-9, 1e9);
        offset = new Vector(ActualWidth / 2 - (b.Left + b.Width / 2) * scale,
            ActualHeight / 2 + (b.Top + b.Height / 2) * scale);
        UpdateCamera(redrawGeometry: true);
    }
    public void ClearSelection()
    {
        selected.Clear(); selectionGeometry.Clear(); Measurement = null;
        SetHoveredMidpoint(null);
        DrawSelection(); DrawMeasurement();
        SelectionChanged?.Invoke();
    }
    private Point World(Point p) => new((p.X - offset.X) / scale, (offset.Y - p.Y) / scale);
    private Point Screen(Point p) => new(p.X * scale + offset.X, -p.Y * scale + offset.Y);
    private static Pen Pen(Brush brush, double width) { var pen = new Pen(brush, width); pen.Freeze(); return pen; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (drawing == null)
        {
            var text = new FormattedText("Open or drop a DXF drawing\n\nCtrl+O  ·  Wheel to zoom  ·  Middle/right drag to pan",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20,
                Brushes.LightSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Max(20, (ActualWidth - text.Width) / 2), Math.Max(20, ActualHeight / 2 - 50)));
            return;
        }
    }
    // Retain the world-space drawing. Panning only updates the parent transform;
    // reopening these visuals would invalidate their expensive content bounds.
    private void DrawBase()
    {
        using var dc = baseVisual.RenderOpen();
        if (drawing != null) dc.DrawGeometry(null, Pen(Brushes.LightGray, 1 / scale), drawing);
    }
    private void DrawSelection()
    {
        using var dc = selectionVisual.RenderOpen();
        for (int i = 0; i < selected.Count; i++)
            if (selected[i].Midpoint == null)
                dc.DrawGeometry(null, Pen(i == 0 ? Brushes.Orange : Brushes.DeepSkyBlue, 3 / scale), selectionGeometry[selected[i]]);
    }
    private void UpdateCamera(bool redrawGeometry = false)
    {
        cameraTransform.Matrix = new Matrix(scale, 0, 0, -scale, offset.X, offset.Y);
        // Zoom/fit still refresh pen widths to preserve constant screen-space strokes.
        // Translation leaves both retained world-space visuals untouched.
        if (redrawGeometry) { DrawBase(); DrawSelection(); }
        SetHoveredMidpoint(null);
        if (selected.Count > 0) DrawMeasurement();
    }
    private void DrawMeasurement()
    {
        using var dc = measurementVisual.RenderOpen();
        for (int i = 0; i < selected.Count; i++)
            if (selected[i].Midpoint is Point point)
                DrawMidpointMarker(dc, Screen(point), i == 0 ? Brushes.Orange : Brushes.DeepSkyBlue);
        if (Measurement is { } m)
        {
            var a = Screen(m.A); var b = Screen(m.B);
            dc.DrawLine(MeasurementPen, a, b);
            dc.DrawEllipse(Brushes.MediumSpringGreen, null, a, 4, 4);
            dc.DrawEllipse(Brushes.MediumSpringGreen, null, b, 4, 4);
            var text = new FormattedText(m.Distance.ToString("0.######", CultureInfo.CurrentCulture),
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 14,
                Brushes.MediumSpringGreen, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var at = new Point(Math.Clamp((a.X + b.X) / 2 + 10, 0, Math.Max(0, ActualWidth - text.Width - 8)),
                Math.Clamp((a.Y + b.Y) / 2 - 24, 0, Math.Max(0, ActualHeight - text.Height - 8)));
            dc.DrawRectangle(LabelBrush, null,
                new Rect(at.X - 4, at.Y - 2, text.Width + 8, text.Height + 4));
            dc.DrawText(text, at);
        }
    }
    private static void DrawMidpointMarker(DrawingContext dc, Point point, Brush color)
    {
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(point.X, point.Y - 7), true, true);
            ctx.LineTo(new Point(point.X - 7, point.Y + 5), true, false);
            ctx.LineTo(new Point(point.X + 7, point.Y + 5), true, false);
        }
        triangle.Freeze();
        dc.DrawGeometry(BackgroundBrush, Pen(color, 2), triangle);
        dc.DrawEllipse(color, null, point, 1.5, 1.5);
    }
    private void SetHoveredMidpoint(MeasurementTarget? target)
    {
        if (Equals(hoveredMidpoint, target)) return;
        hoveredMidpoint = target;
        using var dc = snapVisual.RenderOpen();
        if (target?.Midpoint is not Point point) return;
        var screen = Screen(point);
        DrawMidpointMarker(dc, screen, Brushes.YellowGreen);
        var text = new FormattedText("Midpoint", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.YellowGreen, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var label = new Point(Math.Clamp(screen.X + 12, 2, Math.Max(2, ActualWidth - text.Width - 6)),
            Math.Clamp(screen.Y - text.Height - 8, 2, Math.Max(2, ActualHeight - text.Height - 6)));
        dc.DrawRectangle(LabelBrush, null, new Rect(label.X - 2, label.Y - 2, text.Width + 4, text.Height + 4));
        dc.DrawText(text, label);
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        SetHoveredMidpoint(null); base.OnMouseLeave(e);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Document == null) return;
        var at = e.GetPosition(this); var world = World(at);
        scale = Math.Clamp(scale * Math.Pow(1.18, e.Delta / 120.0), 1e-9, 1e9);
        offset = new Vector(at.X - world.X * scale, at.Y + world.Y * scale);
        UpdateCamera(redrawGeometry: true); e.Handled = true;
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
            foreach (var entity in Document.Entities)
            {
                var bounds = entity.Bounds; bounds.Inflate(nearest, nearest);
                if (!bounds.Contains(point)) continue;
                var distance = entity.Curves.Min(c => (c.Closest(point) - point).Length);
                if (distance < nearest) { target = new MeasurementTarget(entity); nearest = distance; }
            }
        }
        if (target == null) { ClearSelection(); return; }
        if (selected.Contains(target)) { selected.Remove(target); selectionGeometry.Remove(target); }
        else
        {
            if (selected.Count == 2) { selected.Clear(); selectionGeometry.Clear(); }
            selected.Add(target);
            if (target.Midpoint == null) selectionGeometry[target] = Build(target.Entity.Curves);
        }
        Measurement = selected.Count == 2 ? selected[0].Distance(selected[1]) : null;
        DrawSelection(); DrawMeasurement(); SetHoveredMidpoint(null);
        SelectionChanged?.Invoke();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var next = e.GetPosition(this);
        if (panStart is Point start)
        {
            offset += next - start; panStart = next; UpdateCamera();
        }
        else SetHoveredMidpoint(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? null : midpointIndex?.Find(World(next), 8 / scale));
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        { panStart = null; ReleaseMouseCapture(); Cursor = Cursors.Cross; }
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { panStart = null; Cursor = Cursors.Cross; base.OnLostMouseCapture(e); }
}
