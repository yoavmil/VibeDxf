using System.Windows;
using System.Windows.Controls;

namespace VibeDxf.Wpf;

/// <summary>A selected source entity/anchor and optional viewport frame used only for display placement.</summary>
public sealed record DxfSelection(MeasurementAnchor Anchor, DxfEntity Entity, DxfEntityId? ViewportFrameId)
{
    /// <summary>Returns a short host-display description honoring midpoint and eligible Center strategies.</summary>
    public string Describe(MeasurementStrategy strategy) => Anchor.MidpointSegment != null ? $"{Entity.Kind} midpoint" :
        strategy == MeasurementStrategy.Center && Entity.Kind is "ARC" or "CIRCLE" && Entity.Curves.Count == 1 && !Entity.Curves[0].IsLine
            ? $"{Entity.Kind} center" : Entity.Kind;
}
/// <summary>Positive finite zoom scale and control-relative DIP offset for the y-up world-to-screen camera.</summary>
public sealed record DxfCamera(double Scale, Vector Offset);
/// <summary>Immutable selection and measurement state associated with a committed document revision.</summary>
public class DxfViewStateEventArgs(DxfDocument? document, IReadOnlyList<DxfSelection> selection, MeasurementResult? measurement) : EventArgs
{
    /// <summary>The committed prepared document, or null when cleared.</summary>
    public DxfDocument? Document { get; } = document;
    /// <summary>Identity of the immutable snapshot represented by this state.</summary>
    public Guid? RevisionId => Document?.RevisionId;
    /// <summary>Read-only ordered selection snapshot at the time of the event.</summary>
    public IReadOnlyList<DxfSelection> Selection { get; } = selection;
    /// <summary>Current two-target measurement, or null when fewer than two targets are selected.</summary>
    public MeasurementResult? Measurement { get; } = measurement;
}
/// <summary>Previous and newly committed document, or null after clearing.</summary>
public sealed class DxfDocumentChangedEventArgs(DxfDocument? oldDocument, DxfDocument? document) : EventArgs
{
    /// <summary>The document installed before this change, or null.</summary>
    public DxfDocument? OldDocument { get; } = oldDocument;
    /// <summary>The committed prepared document, or null when cleared.</summary>
    public DxfDocument? Document { get; } = document;
    /// <summary>Identity of the immutable snapshot represented by this state.</summary>
    public Guid? RevisionId => Document?.RevisionId;
}
/// <summary>Previous and current paper layout IDs; null represents model space.</summary>
public sealed class DxfLayoutChangedEventArgs(DxfDocument? document, DxfLayoutId? oldLayout, DxfLayoutId? layout) : EventArgs
{
    /// <summary>The committed prepared document, or null when cleared.</summary>
    public DxfDocument? Document { get; } = document;
    /// <summary>Identity of the immutable snapshot represented by this state.</summary>
    public Guid? RevisionId => Document?.RevisionId;
    /// <summary>Previously displayed paper layout, or null for model space.</summary>
    public DxfLayoutId? OldLayout { get; } = oldLayout;
    /// <summary>Currently displayed paper layout, or null for model space.</summary>
    public DxfLayoutId? Layout { get; } = layout;
}
/// <summary>Immutable per-view layer/category visibility and monochrome settings.</summary>
public sealed class DxfVisibilityChangedEventArgs(DxfDocument? document, IReadOnlyList<bool> layers,
    IReadOnlyList<EntityCategory> hiddenCategories, bool monochrome) : EventArgs
{
    /// <summary>The committed prepared document, or null when cleared.</summary>
    public DxfDocument? Document { get; } = document;
    /// <summary>Identity of the immutable snapshot represented by this state.</summary>
    public Guid? RevisionId => Document?.RevisionId;
    /// <summary>Read-only per-layer enabled flags in document layer order at the time of the event.</summary>
    public IReadOnlyList<bool> Layers { get; } = layers;
    /// <summary>Categories disabled in this viewer at the time of the event.</summary>
    public IReadOnlyList<EntityCategory> HiddenCategories { get; } = hiddenCategories;
    /// <summary>Whether this viewer draws with one display color instead of source colors.</summary>
    public bool Monochrome { get; } = monochrome;
}
/// <summary>Native device or rendering failure reported to the host; automatic device recovery is not guaranteed.</summary>
public sealed class DxfRenderingFailedEventArgs(Exception exception) : EventArgs
{
    /// <summary>The underlying native/device/rendering exception for host-controlled error presentation.</summary>
    public Exception Exception { get; } = exception;
}

/// <summary>Embeddable Windows/WPF DXF viewer. Use its dispatcher; documents already contain prepared text geometry.</summary>
public sealed class DxfView : Border, IDisposable
{
    private readonly DxfViewport viewport = new();
    private CancellationTokenSource? pending;
    private long generation;
    private bool disposed, batching, syncingLayout;
    private IReadOnlyList<DxfSelection> selection = Array.Empty<DxfSelection>();
    private DxfLayoutId? publishedLayout;

    /// <summary>Creates an empty viewer on its owning STA WPF dispatcher; native resources follow load/unload.</summary>
    public DxfView()
    {
        Child = viewport;
        viewport.SelectionChanged += () => { if (!batching) PublishSelection(); };
        viewport.LayoutChanged += () => { if (!batching) PublishLayout(); };
        viewport.VisibilityChanged += () => { if (!batching) PublishVisibility(); };
        viewport.RenderingFailed += ex => RenderingFailed?.Invoke(this, new(ex));
    }
    private static readonly DependencyPropertyKey DocumentPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Document), typeof(DxfDocument), typeof(DxfView), new PropertyMetadata(null));
    /// <summary>Read-only dependency property identifying the committed document.</summary>
    public static readonly DependencyProperty DocumentProperty = DocumentPropertyKey.DependencyProperty;
    /// <summary>The committed prepared document, or null when cleared.</summary>
    public DxfDocument? Document => (DxfDocument?)GetValue(DocumentProperty);
    /// <summary>Read-only ordered selection snapshot; previous snapshots remain unchanged by later interactions.</summary>
    public IReadOnlyList<DxfSelection> Selected => selection;
    /// <summary>Current two-target measurement, or null when fewer than two targets are selected.</summary>
    public MeasurementResult? Measurement { get; private set; }
    /// <summary>Raised on the dispatcher after a document assignment or clear commits.</summary>
    public event EventHandler<DxfDocumentChangedEventArgs>? DocumentChanged;
    /// <summary>Raised with the committed ordered selection snapshot.</summary>
    public event EventHandler<DxfViewStateEventArgs>? SelectionChanged;
    /// <summary>Raised after selection or strategy changes with the current nullable result.</summary>
    public event EventHandler<DxfViewStateEventArgs>? MeasurementChanged;
    /// <summary>Raised after the active model/paper view changes.</summary>
    public event EventHandler<DxfLayoutChangedEventArgs>? LayoutChanged;
    /// <summary>Raised with immutable layer/category/monochrome settings.</summary>
    public event EventHandler<DxfVisibilityChangedEventArgs>? VisibilityChanged;
    /// <summary>Reports native/device/rendering failure without opening a host dialog.</summary>
    public event EventHandler<DxfRenderingFailedEventArgs>? RenderingFailed;

    /// <summary>Dependency property for the first selection strategy.</summary>
    public static readonly DependencyProperty FirstMeasurementStrategyProperty = StrategyProperty(nameof(FirstMeasurementStrategy));
    /// <summary>Dependency property for the second selection strategy.</summary>
    public static readonly DependencyProperty SecondMeasurementStrategyProperty = StrategyProperty(nameof(SecondMeasurementStrategy));
    private static DependencyProperty StrategyProperty(string name) => DependencyProperty.Register(name, typeof(MeasurementStrategy), typeof(DxfView),
        new PropertyMetadata(MeasurementStrategy.Closest, (d, _) => ((DxfView)d).ApplyStrategies()), value => Enum.IsDefined((MeasurementStrategy)value));
    /// <summary>First selection strategy; changing it immediately recalculates measurement.</summary>
    public MeasurementStrategy FirstMeasurementStrategy { get => (MeasurementStrategy)GetValue(FirstMeasurementStrategyProperty); set => SetValue(FirstMeasurementStrategyProperty, value); }
    /// <summary>Second selection strategy; changing it immediately recalculates measurement.</summary>
    public MeasurementStrategy SecondMeasurementStrategy { get => (MeasurementStrategy)GetValue(SecondMeasurementStrategyProperty); set => SetValue(SecondMeasurementStrategyProperty, value); }
    private void ApplyStrategies()
    {
        VerifyUsable();
        batching = true;
        try { viewport.FirstStrategy = FirstMeasurementStrategy; viewport.SecondStrategy = SecondMeasurementStrategy; }
        finally { batching = false; }
        PublishSelection();
    }

    /// <summary>Dependency property for a source layout ID, or null for model space.</summary>
    public static readonly DependencyProperty ActiveLayoutProperty = DependencyProperty.Register(nameof(ActiveLayout), typeof(DxfLayoutId?), typeof(DxfView),
        new PropertyMetadata(null, (d, e) => ((DxfView)d).ApplyLayout((DxfLayoutId?)e.NewValue, (DxfLayoutId?)e.OldValue)));
    /// <summary>Active source layout ID or null for model space; foreign layouts are rejected.</summary>
    public DxfLayoutId? ActiveLayout { get => (DxfLayoutId?)GetValue(ActiveLayoutProperty); set => SetValue(ActiveLayoutProperty, value); }
    private void ApplyLayout(DxfLayoutId? id, DxfLayoutId? previous)
    {
        if (syncingLayout) return;
        VerifyUsable();
        int index = id == null ? -1 : Document?.Layouts.ToList().FindIndex(l => l.Id == id) ?? -1;
        if (id != null && index < 0)
        {
            syncingLayout = true;
            try { SetCurrentValue(ActiveLayoutProperty, previous); } finally { syncingLayout = false; }
            throw new ArgumentException("Layout must belong to the assigned document.", nameof(id));
        }
        viewport.ActiveLayout = index;
    }
    /// <summary>Dependency property controlling per-view monochrome display.</summary>
    public static readonly DependencyProperty MonochromeProperty = DependencyProperty.Register(nameof(Monochrome), typeof(bool), typeof(DxfView),
        new PropertyMetadata(false, (d, e) => { var view = (DxfView)d; view.VerifyUsable(); view.viewport.Monochrome = (bool)e.NewValue; }));
    /// <summary>Whether this viewer draws with one display color instead of source colors.</summary>
    public bool Monochrome { get => (bool)GetValue(MonochromeProperty); set => SetValue(MonochromeProperty, value); }

    /// <summary>Installs a fully prepared immutable document on the owning dispatcher. A newer assignment supersedes older work; cancellation or allocation failure retains the previous drawing.</summary>
    public async Task SetDocumentAsync(DxfDocument document, CancellationToken cancellationToken = default)
    {
        VerifyUsable(); ArgumentNullException.ThrowIfNull(document);
        var operation = Begin(cancellationToken); var revision = generation;
        try { await Install(document, revision, operation.Token).ConfigureAwait(false); }
        finally { await Finish(operation).ConfigureAwait(false); }
    }
    /// <summary>Uses the same document loader as document-only consumers. A newer assignment supersedes this entire operation.</summary>
    public async Task<DxfLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        VerifyUsable(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var operation = Begin(cancellationToken); var revision = generation;
        try
        {
            var result = await DxfDocument.LoadAsync(path, operation.Token).ConfigureAwait(false);
            await Install(result.Document, revision, operation.Token).ConfigureAwait(false);
            return result;
        }
        finally { await Finish(operation).ConfigureAwait(false); }
    }
    private CancellationTokenSource Begin(CancellationToken token)
    {
        pending?.Cancel(); ++generation;
        return pending = CancellationTokenSource.CreateLinkedTokenSource(token);
    }
    private async Task Finish(CancellationTokenSource operation)
    {
        await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(pending, operation)) pending = null; });
        operation.Dispose();
    }
    private async Task Install(DxfDocument document, long revision, CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested(); VerifyUsable();
            if (revision != generation) throw new OperationCanceledException(token);
            if (ReferenceEquals(Document, document)) return;
            var old = Document;
            batching = true;
            try { viewport.SetDocument(document, () => SetValue(DocumentPropertyKey, document), token); }
            finally { batching = false; }
            RefreshSelection(); SyncLayout();
            DocumentChanged?.Invoke(this, new(old, document));
            PublishSelection(); PublishLayout(); PublishVisibility();
        });
    }
    private void RefreshSelection()
    {
        selection = Array.AsReadOnly(viewport.Selected.Select(t => new DxfSelection(new(t.Entity.Id, t.MidpointSegment), t.Entity, t.Viewport?.FrameEntityId)).ToArray());
        Measurement = selection.Count == 2 ? Document!.Measure(selection[0].Anchor, selection[1].Anchor, FirstMeasurementStrategy, SecondMeasurementStrategy) : null;
    }
    private void PublishSelection()
    {
        RefreshSelection(); var args = new DxfViewStateEventArgs(Document, selection, Measurement);
        SelectionChanged?.Invoke(this, args); MeasurementChanged?.Invoke(this, args);
    }
    private void SyncLayout()
    {
        syncingLayout = true;
        try { SetCurrentValue(ActiveLayoutProperty, viewport.ActiveLayout < 0 ? null : Document!.Layouts[viewport.ActiveLayout].Id); }
        finally { syncingLayout = false; }
    }
    private void PublishLayout()
    {
        var old = publishedLayout; SyncLayout(); publishedLayout = ActiveLayout;
        LayoutChanged?.Invoke(this, new(Document, old, ActiveLayout));
    }
    private void PublishVisibility() => VisibilityChanged?.Invoke(this, new(Document,
        Array.AsReadOnly((Document?.Layers.Select((_, i) => viewport.IsLayerVisible(i)) ?? []).ToArray()),
        Array.AsReadOnly(Enum.GetValues<EntityCategory>().Where(c => !viewport.IsCategoryVisible(c)).ToArray()), Monochrome));

    /// <summary>Toggles an interactively selectable source anchor, optionally displayed through an active paper viewport. Invalid, hidden, annotation or incompatible targets are rejected.</summary>
    public void Select(MeasurementAnchor anchor, DxfEntityId? viewportFrameId = null)
    {
        VerifyUsable();
        var entity = Document?.FindEntity(anchor.EntityId);
        var window = Window(viewportFrameId);
        if (entity == null || entity.IsAnnotation || !viewport.IsEntityVisible(entity) ||
            Document!.Measure(anchor, anchor).Status != MeasurementStatus.Success ||
            (window == null ? entity.CoordinateContext.LayoutId != ActiveLayout : entity.CoordinateContext.LayoutId != null) ||
            window?.FrozenLayers.Contains(entity.LayerId) == true)
            throw new ArgumentException("Target must be selectable in the active view.", nameof(anchor));
        viewport.SelectTarget(new(entity, anchor.MidpointSegment, window));
    }
    private DxfViewportWindow? Window(DxfEntityId? frameId)
    {
        if (frameId == null) return null;
        return Document?.Layouts.FirstOrDefault(l => l.Id == ActiveLayout)?.Viewports.FirstOrDefault(v => v.FrameEntityId == frameId)
            ?? throw new ArgumentException("Viewport must belong to the active layout.", nameof(frameId));
    }
    /// <summary>Returns the enabled state for a valid layer in the assigned document.</summary>
    public bool IsLayerVisible(int id) { ValidateLayer(id); return viewport.IsLayerVisible(id); }
    /// <summary>Returns whether the category is enabled in this viewer.</summary>
    public bool IsCategoryVisible(EntityCategory category) { VerifyUsable(); ValidateCategory(category); return viewport.IsCategoryVisible(category); }
    /// <summary>Changes one layer mask without rebuilding geometry; hidden targets are removed from picking/selection.</summary>
    public void SetLayerVisible(int id, bool visible) { ValidateLayer(id); viewport.SetLayerVisible(id, visible); }
    /// <summary>Atomically validates a set of layer indices and applies their enabled mask in one visibility update.</summary>
    public void SetLayersVisible(IEnumerable<int> ids, bool visible)
    {
        VerifyUsable(); ArgumentNullException.ThrowIfNull(ids); var array = ids.ToArray();
        foreach (var id in array) ValidateLayer(id);
        viewport.SetLayersVisible(array, visible);
    }
    /// <summary>Changes the category mask without reuploading geometry.</summary>
    public void SetCategoryVisible(EntityCategory category, bool visible) { VerifyUsable(); ValidateCategory(category); viewport.SetCategoryVisible(category, visible); }
    /// <summary>Enables all layers and categories; per-viewport frozen layers still apply.</summary>
    public void ShowAll() { VerifyUsable(); viewport.ShowAll(); }
    /// <summary>Restores layer on/off/frozen masks saved in the file; category and monochrome settings are retained.</summary>
    public void ApplySavedLayerState() { VerifyUsable(); viewport.ApplySavedLayerState(); }
    /// <summary>Enables only the specified layer; category masks remain independent.</summary>
    public void IsolateLayer(int id) { ValidateLayer(id); viewport.IsolateLayer(id); }
    /// <summary>Fits current model or paper bounds into the control using a finite camera.</summary>
    public void FitToDrawing() { VerifyUsable(); viewport.Fit(); }
    /// <summary>Clears selection, measurement and midpoint hover state.</summary>
    public void ClearSelection() { VerifyUsable(); viewport.ClearSelection(); }
    /// <summary>Finite positive scale and DIP offset. Access on the owning dispatcher.</summary>
    public DxfCamera Camera
    {
        get { VerifyUsable(); var camera = viewport.Camera; return new(camera.Scale, camera.Offset); }
        set
        {
            VerifyUsable(); ArgumentNullException.ThrowIfNull(value);
            if (!double.IsFinite(value.Scale) || value.Scale < 1e-9 || value.Scale > 1e9 || !double.IsFinite(value.Offset.X) || !double.IsFinite(value.Offset.Y))
                throw new ArgumentOutOfRangeException(nameof(value));
            viewport.Camera = (value.Scale, value.Offset);
        }
    }
    /// <summary>Converts DXF XY coordinates to control-relative DIPs, optionally through an active source viewport frame.</summary>
    public Point WorldToScreen(Point point, DxfEntityId? viewportFrameId = null)
    {
        VerifyUsable(); ValidatePoint(point); return viewport.ToScreen(Window(viewportFrameId)?.ModelToPaper.Transform(point) ?? point);
    }
    /// <summary>Converts control-relative DIPs to DXF XY coordinates, optionally into model space through an active viewport.</summary>
    public Point ScreenToWorld(Point point, DxfEntityId? viewportFrameId = null)
    {
        VerifyUsable(); ValidatePoint(point); point = viewport.ToWorld(point);
        if (Window(viewportFrameId) is not { } window) return point;
        var inverse = window.ModelToPaper; inverse.Invert(); return inverse.Transform(point);
    }
    /// <summary>Cancels pending generations and releases drawing geometry/source references; later assignment remains supported.</summary>
    public void ClearDocument()
    {
        VerifyUsable(); pending?.Cancel(); ++generation;
        var old = Document; batching = true;
        try { viewport.ClearDocument(); SetValue(DocumentPropertyKey, null); }
        finally { batching = false; }
        RefreshSelection(); SyncLayout();
        DocumentChanged?.Invoke(this, new(old, null)); PublishSelection(); PublishLayout(); PublishVisibility();
    }
    /// <summary>Permanently cancels work and releases native/managed drawing state. Idempotent and dispatcher-affine; dispose before dispatcher shutdown.</summary>
    public void Dispose()
    {
        Dispatcher.VerifyAccess(); if (disposed) return;
        try { ClearDocument(); }
        finally { viewport.ReleaseResources(); disposed = true; GC.SuppressFinalize(this); }
    }
    private void ValidateLayer(int id)
    {
        VerifyUsable(); if (Document == null || id < 0 || id >= Document.Layers.Count) throw new ArgumentOutOfRangeException(nameof(id));
    }
    private static void ValidateCategory(EntityCategory category) { if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category)); }
    private static void ValidatePoint(Point point) { if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) throw new ArgumentOutOfRangeException(nameof(point)); }
    private void VerifyUsable() { Dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(disposed, this); }
}
