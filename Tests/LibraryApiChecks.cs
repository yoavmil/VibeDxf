using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VibeDxf.Wpf;
using Xunit;

[Collection("Visual checks")]
public sealed class LibraryApiChecks
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static DxfDocument Load(string name) => DxfDocument.LoadAsync(Fixture(name)).GetAwaiter().GetResult().Document;
    internal static void Await(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static DxfViewport Inner(DxfView view) => (DxfViewport)view.Child;
    private static DrawingRenderer? Renderer(DxfView view) =>
        ((SwapChainHost)typeof(DxfViewport).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Inner(view))!).Renderer;
    private static object? Buffer(DrawingRenderer renderer) => typeof(DrawingRenderer).GetField("lineBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer);

    [Fact]
    public void PreparedTextAndAnnotationsAreMeasurableWithoutAControl() => TestSupport.Run("Library API: text and annotations without a control", () =>
    {
        var document = Load("Public/uncommon.dxf");
        Assert.Null(Application.Current);
        var text = document.Entities.First(e => e.Kind == "MTEXT");
        var annotation = document.Entities.First(e => e.IsAnnotation);
        Assert.NotEmpty(text.Curves); Assert.NotEmpty(text.FillContours); Assert.True(text.IsApproximate);
        var result = document.Measure(new(text.Id), new(annotation.Id));
        Assert.Equal(MeasurementStatus.Success, result.Status); Assert.True(result.IsApproximate);
        Assert.Equal(text.Distance(annotation), result.Pair); Assert.True(result.Distance >= 0);
        TestSupport.Near(result.Pair!.Value.B.X - result.Pair.Value.A.X, result.DeltaX!.Value);
        TestSupport.Near(result.Pair.Value.B.Y - result.Pair.Value.A.Y, result.DeltaY!.Value);
        Assert.All(document.AllEntities, e => Assert.Same(e, document.FindEntity(e.Id)));
    });

    [Fact]
    public void PublishedDataAndNestedGeometryAreReadOnly() => TestSupport.Run("Library API: immutable document and nested geometry", () =>
    {
        var document = Load("Public/uncommon.dxf"); var glyph = document.Entities.First(e => e.Kind == "MTEXT");
        Assert.Throws<NotSupportedException>(() => ((IList<DxfEntity>)document.Entities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Curve>)glyph.Curves).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Point>)glyph.FillContours[0])[0] = new(123, 456));
        Assert.Throws<NotSupportedException>(() => ((IList<IReadOnlyList<Point>>)glyph.FillContours).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)document.Skipped).Clear());
        var layoutDoc = Load("Public/viewport_frozen_layers.dxf");
        var window = layoutDoc.Layouts[0].Viewports.First(v => v.FrozenLayers.Count > 0);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)window.FrozenLayers).Clear());
        Assert.Equal(EntityCategory.ViewportFrame, layoutDoc.FindEntity(window.FrameEntityId)!.Category);
        var exported = typeof(DxfView).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, t => t.Name is "DrawingRenderer" or "DxfViewport" or "DrawingBuilder" or "DxfReader" or "MeasurementTarget");
        Assert.Empty(typeof(DxfEntity).GetConstructors());
        Assert.All(typeof(DxfEntity).GetProperties(), p => Assert.False(p.SetMethod?.IsPublic ?? false));
    });

    [Fact]
    public void MeasurementsRejectForeignMissingInvalidAndMixedSpaceTargets() => TestSupport.Run("Library API: identity and measurement failures", () =>
    {
        var document = Load("arc-highlight.dxf"); var other = Load("arc-highlight.dxf");
        var arc = document.Entities[0]; var line = document.Entities[1];
        Assert.NotEqual(document.DocumentId, other.DocumentId);
        Assert.Equal(MeasurementStatus.ForeignDocument, document.Measure(new(arc.Id), new(other.Entities[0].Id)).Status);
        Assert.Equal(MeasurementStatus.EntityNotFound, document.Measure(new(new(document.DocumentId, long.MaxValue)), new(line.Id)).Status);
        Assert.Equal(MeasurementStatus.InvalidAnchor, document.Measure(default, new(line.Id)).Status);
        Assert.Equal(MeasurementStatus.InvalidAnchor, document.Measure(new(arc.Id, 0), new(line.Id)).Status);
        Assert.Equal(MeasurementStatus.InvalidAnchor, document.Measure(new(arc.Id), new(line.Id), (MeasurementStrategy)99).Status);
        var centered = document.Measure(new(arc.Id), new(line.Id, 0), MeasurementStrategy.Center);
        Assert.Equal(new Point(0, 0), centered.Pair!.Value.A); Assert.Equal(new Point(0, -12), centered.Pair.Value.B);
        Assert.Equal(12, centered.Distance);
        var sheets = Load("Public/api-cw750-details.dxf");
        Assert.Equal(MeasurementStatus.CoordinateMismatch, sheets.Measure(new(sheets.Entities[0].Id), new(sheets.Layouts[0].Entities[0].Id)).Status);
        Assert.Equal(MeasurementStatus.CoordinateMismatch, sheets.Measure(new(sheets.Layouts[0].Entities[0].Id), new(sheets.Layouts[1].Entities[0].Id)).Status);
        Assert.All(sheets.AllEntities, e => Assert.Equal(e.Handle[(e.Handle.LastIndexOf('/') + 1)..], e.SourceHandle));
    });

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public bool Disposed;
        public override int Read(byte[] buffer, int offset, int count) { cancellation.Cancel(); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { cancellation.Cancel(); return base.Read(buffer); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    [Fact]
    public void StreamOwnershipAndCancellationAreHonored() => TestSupport.Run("Library API: stream ownership and mid-read cancellation", () =>
    {
        var bytes = File.ReadAllBytes(Fixture("arc-highlight.dxf"));
        using var kept = new MemoryStream(bytes); var loaded = DxfDocument.LoadAsync(kept).GetAwaiter().GetResult(); Assert.True(kept.CanRead);
        var owned = new MemoryStream(bytes); DxfDocument.LoadAsync(owned, false).GetAwaiter().GetResult(); Assert.False(owned.CanRead);
        using var cancellation = new CancellationTokenSource(); var stream = new CancellingStream(bytes, cancellation);
        Assert.ThrowsAny<OperationCanceledException>(() => DxfDocument.LoadAsync(stream, false, cancellation.Token).GetAwaiter().GetResult()); Assert.True(stream.Disposed);
        var alreadyCancelled = new MemoryStream(bytes);
        Assert.ThrowsAny<OperationCanceledException>(() => DxfDocument.LoadAsync(alreadyCancelled, false, cancellation.Token).GetAwaiter().GetResult()); Assert.False(alreadyCancelled.CanRead);
        var malformed = new MemoryStream(Encoding.UTF8.GetBytes("0\nSECTION\n2\n"));
        Assert.Throws<InvalidDataException>(() => DxfDocument.LoadAsync(malformed, false).GetAwaiter().GetResult()); Assert.False(malformed.CanRead);
        Assert.Empty(loaded.Diagnostics);
    });

    [Fact]
    public void ColorsAndBlockIdentityRetainSourceMetadata() => TestSupport.Run("Library API: raw colors and block identities", () =>
    {
        var document = Load("Public/colors.dxf");
        var blue = document.Layers.Single(l => l.Name == "BLUE");
        Assert.Equal(DxfColors.FromAci(170), blue.Color); Assert.Equal(170, blue.ColorIndex);
        Assert.NotEqual(blue.Color, DxfDisplayColors.ForDarkBackground(blue.Color));
        var trueColor = document.Entities.Single(e => e.Handle == "220");
        Assert.Equal(0xF26722, trueColor.ColorSource.TrueColor); Assert.Equal(30, trueColor.ColorSource.Index);
        var child = document.Entities.First(e => e.Handle.StartsWith("302["));
        Assert.NotEqual(child.SourceHandle, child.Handle); Assert.NotNull(child.InstancePath);
        Assert.Equal(document.AllEntities.Count, document.AllEntities.Select(e => e.Id).Distinct().Count());
    });

    [Fact]
    public void TwoViewsShareImmutableDataAndReleaseResourcesIndependently() => TestSupport.Run("Library API: independent viewers, resize, DPI and reload", () => TestSupport.Sta(() =>
    {
        var document = Load("arc-highlight.dxf"); using var first = new DxfView(); using var second = new DxfView();
        var panel = new Grid(); panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new());
        panel.Children.Add(first); panel.Children.Add(second); Grid.SetColumn(second, 1);
        var window = new Window { Content = panel, Width = 1000, Height = 600, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); Await(first.SetDocumentAsync(document)); Await(second.SetDocumentAsync(document)); Pump();
            Assert.Same(first.Document, second.Document); var renderer = Renderer(first)!; var buffer = Buffer(renderer);
            first.Select(new(document.Entities[0].Id)); first.Select(new(document.Entities[1].Id));
            Assert.Equal(2, first.Selected.Count); Assert.Empty(second.Selected);
            var previousSelection = first.Selected; int events = 0;
            first.MeasurementChanged += (_, e) => { Assert.Equal(e.Document?.RevisionId, e.RevisionId); events++; };
            first.FirstMeasurementStrategy = MeasurementStrategy.Center; Assert.Equal(1, events);
            Assert.Equal(new Point(0, 0), first.Measurement!.Pair!.Value.A);
            first.SetLayerVisible(document.Entities[0].LayerId, false); Assert.Single(first.Selected); Assert.Equal(2, previousSelection.Count);
            Assert.True(second.IsLayerVisible(document.Entities[0].LayerId)); Assert.True(document.Measure(previousSelection[0].Anchor, previousSelection[1].Anchor).Distance >= 0);
            first.Camera = new(2, new(100, 200)); Assert.NotEqual(first.Camera, second.Camera);
            Assert.Equal(new Point(3, 7), first.ScreenToWorld(first.WorldToScreen(new(3, 7))));
            first.Monochrome = true; window.Width = 1200; Pump(); Assert.Same(buffer, Buffer(renderer));
            typeof(FrameworkElement).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Inner(first), [new DpiScale(1, 1), new DpiScale(1.5, 1.5)]);
            Pump(); Assert.Same(buffer, Buffer(renderer));
            panel.Children.Remove(first); Pump(); Assert.Null(Renderer(first)); Assert.NotNull(Renderer(second));
            panel.Children.Add(first); Pump(); Assert.NotSame(renderer, Renderer(first)); Assert.Same(document, first.Document);
            first.ClearDocument(); Pump(); Assert.Null(first.Document); Assert.Empty(first.Selected);
            Await(first.SetDocumentAsync(document)); Pump(); Assert.NotNull(Renderer(first));
        }
        finally { window.Close(); Pump(); }
        Assert.Null(Renderer(first)); Assert.Null(Renderer(second));
    }));

    [Fact]
    public void ViewerLoadReplacementFailureAndDisposalAreAtomic() => TestSupport.Run("Library API: replacement, failure retention and disposal", () => TestSupport.Sta(() =>
    {
        var original = Load("arc-highlight.dxf"); using var view = new DxfView(); Await(view.SetDocumentAsync(original));
        var older = view.LoadAsync(Fixture("Public/api-cw750-details.dxf")); var newer = view.SetDocumentAsync(original);
        Await(newer); Assert.ThrowsAny<OperationCanceledException>(() => Await(older)); Assert.Same(original, view.Document);
        Assert.Throws<FileNotFoundException>(() => Await(view.LoadAsync(Fixture("missing.dxf")))); Assert.Same(original, view.Document);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Await(view.SetDocumentAsync(Load("Public/uncommon.dxf"), cancellation.Token))); Assert.Same(original, view.Document);
        var pending = view.LoadAsync(Fixture("Public/api-cw750-details.dxf")); view.Dispose();
        Assert.ThrowsAny<OperationCanceledException>(() => Await(pending)); Assert.Null(view.Document);
        view.Dispose(); Assert.Throws<ObjectDisposedException>(() => view.FitToDrawing());
    }));

    [Fact]
    public void FailedCandidateUploadRetainsPreviousDrawingAndBuffers() => TestSupport.Run("Library API: failed GPU replacement preserves current drawing", () => TestSupport.Sta(() =>
    {
        var original = Load("arc-highlight.dxf"); var replacement = Load("Public/uncommon.dxf");
        using var view = new DxfView();
        var window = new Window { Content = view, Width = 700, Height = 500, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show(); Await(view.SetDocumentAsync(original)); Pump();
            var renderer = Renderer(view)!; var buffer = Buffer(renderer); var camera = view.Camera;
            renderer.BeforeDrawingCommit = () => throw new IOException("Simulated candidate allocation failure.");
            Assert.Throws<IOException>(() => Await(view.SetDocumentAsync(replacement)));
            Assert.Same(original, view.Document); Assert.Same(buffer, Buffer(renderer)); Assert.Equal(camera, view.Camera);
            renderer.BeforeDrawingCommit = null; Await(view.SetDocumentAsync(replacement)); Pump();
            Assert.Same(replacement, view.Document); Assert.NotSame(buffer, Buffer(renderer));
        }
        finally { window.Close(); Pump(); }
    }));

    [Fact]
    public void PublicLayoutAndSelectionUseSourceCoordinateContexts() => TestSupport.Run("Library API: paper viewports, frozen layers and context", () => TestSupport.Sta(() =>
    {
        var document = Load("Public/viewport_frozen_layers.dxf"); using var view = new DxfView(); Await(view.SetDocumentAsync(document)); view.ShowAll();
        view.ActiveLayout = document.Layouts[0].Id;
        var window = document.Layouts[0].Viewports.First(v => v.FrozenLayers.Count > 0);
        var hidden = document.Entities.First(e => window.FrozenLayers.Contains(e.LayerId));
        Assert.Throws<ArgumentException>(() => view.Select(new(hidden.Id), window.FrameEntityId));
        var visible = document.Entities.First(e => !e.IsAnnotation && !window.FrozenLayers.Contains(e.LayerId));
        view.Select(new(visible.Id), window.FrameEntityId); Assert.Equal(window.FrameEntityId, view.Selected[0].ViewportFrameId);
        var actual = view.ScreenToWorld(view.WorldToScreen(new(3, 8), window.FrameEntityId), window.FrameEntityId);
        TestSupport.Near(3, actual.X); TestSupport.Near(8, actual.Y);
        view.ActiveLayout = null; Assert.Empty(view.Selected);
        Assert.Throws<ArgumentException>(() => view.ActiveLayout = new DxfLayoutId(Guid.NewGuid(), 0)); Assert.Null(view.ActiveLayout);
    }));
}
