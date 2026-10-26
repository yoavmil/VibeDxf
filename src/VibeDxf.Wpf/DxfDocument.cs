using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace VibeDxf.Wpf;

/// <summary>Document-lineage GUID and occurrence value. IDs are not source handles or persistent file identifiers.</summary>
public readonly record struct DxfEntityId(Guid DocumentId, long Value);
/// <summary>Stable layout identity within a document lineage; distinct from a tab index.</summary>
public readonly record struct DxfLayoutId(Guid DocumentId, int Value);
/// <summary>Model space when LayoutId is null, otherwise the identified paper layout.</summary>
public readonly record struct DxfCoordinateContext(DxfLayoutId? LayoutId);
/// <summary>Severity of a supported approximation or unsupported input feature.</summary>
public enum DxfDiagnosticSeverity
{
    /// <summary>Informational load note.</summary>
    Information,
    /// <summary>Unsupported input or an approximation requiring caller attention.</summary>
    Warning
}
/// <summary>Structured aggregated load note. Codes identify unsupported records or approximations; instance identity may be unavailable.</summary>
public sealed record DxfDiagnostic(string Code, DxfDiagnosticSeverity Severity, string Phase,
    string? EntityKind, string Message, int Count, string? InstanceHandle = null);
/// <summary>A fully prepared document and its immutable load diagnostics.</summary>
public sealed record DxfLoadResult(DxfDocument Document, IReadOnlyList<DxfDiagnostic> Diagnostics);
/// <summary>Source ACI index and optional group-420 true color before layer/block inheritance and theme adjustment.</summary>
public sealed record DxfColorSource(int? Index = null, int? TrueColor = null);

/// <summary>A saved layer definition; visibility settings belong to each viewer.</summary>
public sealed record DxfLayer(string Name, uint Color, bool OnInFile, bool FrozenInFile, bool Plot, string Linetype)
{
    /// <summary>Stable file-order layer index within the loaded document.</summary>
    public int Id { get; internal init; }
    /// <summary>Raw saved layer ACI index, whose sign also indicates the saved on/off state.</summary>
    public int ColorIndex { get; internal init; } = 7;
    /// <summary>Optional raw group-420 color as 0x00RRGGBB.</summary>
    public int? TrueColor { get; internal init; }
    /// <summary>True when the saved layer is on and not frozen; viewer state is independent.</summary>
    public bool VisibleInFile => OnInFile && !FrozenInFile;
}

/// <summary>A source viewport window on a paper layout. Nonplan orthographic views contain display-only projected snapshots.</summary>
/// <param name="Handle">Source VIEWPORT handle.</param>
/// <param name="Paper">Rectangular paper-space clipping window.</param>
/// <param name="ModelToPaper">Maps model XY for plan views, or prepared display-coordinate-system coordinates for nonplan orthographic views; it cannot map arbitrary XYZ using only XY.</param>
public sealed record DxfViewportWindow(string Handle, Rect Paper, Matrix ModelToPaper)
{
    private IReadOnlyList<int> frozenLayers = Array.Empty<int>();
    /// <summary>Read-only layer indices frozen in this viewport only.</summary>
    public IReadOnlyList<int> FrozenLayers { get => frozenLayers; internal init => frozenLayers = Array.AsReadOnly(value.ToArray()); }
    /// <summary>Identity of the generated paper-space frame corresponding to this viewport window.</summary>
    public DxfEntityId FrameEntityId { get; internal set; }
    internal DxfEntity? FrameEntity { get; init; }
    /// <summary>True for a nonplan orthographic view; its projected geometry is display-only.</summary>
    public bool IsOrthographic3D { get; internal init; }
    /// <summary>Whether model XY picking, snapping and coordinate conversion are supported through this window.</summary>
    public bool IsInteractive => CameraView?.IsInteractive ?? !IsOrthographic3D;
    /// <summary>Whether this layout window uses perspective projection.</summary>
    public bool IsPerspective => CameraView?.Perspective ?? false;
    internal DxfModelView? CameraView { get; init; }
    internal bool HasProjection => CameraView != null || IsOrthographic3D;
    internal Func<System.Windows.Media.Media3D.Point3D,Point>? ProjectPoint3D { get; init; }
    internal IReadOnlyList<DxfEntity>? ProjectedEntities { get; set; }
}

/// <summary>A read-only paper sheet containing its own entities and model viewport definitions.</summary>
public sealed class DxfLayout
{
    internal DxfLayout(DxfLayoutId id, LayoutBuilder source)
    {
        Id = id; Name = source.Name;
        Entities = Array.AsReadOnly(source.Entities.ToArray());
        Viewports = Array.AsReadOnly(source.Viewports.ToArray());
        Bounds = source.Bounds;
    }
    /// <summary>Document-local stable identity; do not use collection positions or handles as identity.</summary>
    public DxfLayoutId Id { get; }
    /// <summary>Name stored in the source drawing.</summary>
    public string Name { get; }
    /// <summary>Read-only entities in this coordinate space; document Entities denotes model space only.</summary>
    public IReadOnlyList<DxfEntity> Entities { get; }
    /// <summary>Read-only source viewport definitions belonging to this paper sheet.</summary>
    public IReadOnlyList<DxfViewportWindow> Viewports { get; }
    /// <summary>Finite fit bounds in this coordinate space; unbounded curves contribute their base point only.</summary>
    public Rect Bounds { get; }
}

/// <summary>A fully prepared, read-only Windows/WPF drawing. Reading and measuring require no control or graphics device.</summary>
public sealed class DxfDocument
{
    private readonly IReadOnlyDictionary<DxfEntityId, DxfEntity> byId;
    internal DxfDocument(DrawingBuilder source)
    {
        LoadingContext.Check();
        DocumentId = Guid.NewGuid(); RevisionId = Guid.NewGuid(); Units = source.Units;
        Layers = Array.AsReadOnly(source.Layers.Select((l, i) => l with { Id = i }).ToArray());
        Groups = Array.AsReadOnly(source.Groups.ToArray());
        for(int i=0;i<source.ModelViews.Count;i++)source.ModelViews[i].Id=new(DocumentId,i);
        ModelViews=Array.AsReadOnly(source.ModelViews.ToArray());
        DefaultModelView=source.DefaultModelView?.Id;
        long next = 1;
        void Assign(IEnumerable<DxfEntity> entities, DxfCoordinateContext context)
        {
            foreach (var entity in entities)
            {
                LoadingContext.Check();
                entity.Id = new(DocumentId, next++);
                entity.CoordinateContext = context;
                entity.LayerId = source.Layers.FindIndex(l => l.Name.Equals(entity.Layer, StringComparison.OrdinalIgnoreCase));
            }
        }
        Assign(source.Entities, new(null));
        for (int i = 0; i < source.Layouts.Count; i++)
        {
            var layout = source.Layouts[i];
            Assign(layout.Entities, new(new DxfLayoutId(DocumentId, i)));
            foreach (var window in layout.Viewports) window.FrameEntityId = window.FrameEntity?.Id ?? default;
        }
        Entities = Array.AsReadOnly(source.Entities.ToArray());
        Layouts = Array.AsReadOnly(source.Layouts.Select((l, i) => new DxfLayout(new(DocumentId, i), l)).ToArray());
        AllEntities = Array.AsReadOnly(Entities.Concat(Layouts.SelectMany(l => l.Entities)).ToArray());
        byId = new ReadOnlyDictionary<DxfEntityId, DxfEntity>(AllEntities.ToDictionary(e => e.Id));
        Skipped = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(source.Skipped));
        RenderingWarnings = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(source.RenderingWarnings));
        LoadDiagnostics = Array.AsReadOnly(Skipped.Select(p => new DxfDiagnostic("UnsupportedEntity", DxfDiagnosticSeverity.Warning,
            "Parsing", p.Key, p.Key, p.Value)).Concat(RenderingWarnings.Select(p => new DxfDiagnostic("ApproximateFeature",
            DxfDiagnosticSeverity.Warning, "Preparation", null, p.Key, p.Value))).ToArray());
        Bounds = source.Bounds; UnsupportedWarning = source.UnsupportedWarning;
    }

    /// <summary>Lineage GUID distinguishing unrelated loads, including separate loads of the same file.</summary>
    public Guid DocumentId { get; }
    /// <summary>Identity of the immutable snapshot represented by this state.</summary>
    public Guid RevisionId { get; }
    /// <summary>Unit label from INSUNITS, or an explicit unspecified-units label; coordinates are not converted.</summary>
    public string Units { get; }
    /// <summary>Read-only entities in this coordinate space; document Entities denotes model space only.</summary>
    public IReadOnlyList<DxfEntity> Entities { get; }
    /// <summary>Model entities followed by paper entities in retained layout tab order.</summary>
    public IReadOnlyList<DxfEntity> AllEntities { get; }
    /// <summary>Retained paper layouts in source tab order.</summary>
    public IReadOnlyList<DxfLayout> Layouts { get; }
    /// <summary>Saved VPORT configurations and named VIEW definitions in source order.</summary>
    public IReadOnlyList<DxfModelView> ModelViews { get; }
    /// <summary>The first active model VPORT, or legacy header fallback when no active VPORT exists.</summary>
    public DxfModelViewId? DefaultModelView { get; }
    /// <summary>Read-only saved layer definitions in stable file order; view visibility is independent.</summary>
    public IReadOnlyList<DxfLayer> Layers { get; }
    internal IReadOnlyList<(int Layer, EntityCategory Category)> Groups { get; }
    /// <summary>Aggregated unsupported-record counts from source loading.</summary>
    public IReadOnlyDictionary<string, int> Skipped { get; }
    /// <summary>Aggregated supported-but-approximate feature counts from loading/preparation.</summary>
    public IReadOnlyDictionary<string, int> RenderingWarnings { get; }
    /// <summary>Immutable structured diagnostics describing the source load.</summary>
    public IReadOnlyList<DxfDiagnostic> LoadDiagnostics { get; }
    /// <summary>Finite fit bounds in this coordinate space; unbounded curves contribute their base point only.</summary>
    public Rect Bounds { get; }
    /// <summary>Formatted compatibility summary, or null; the host decides whether and how to present it.</summary>
    public string? UnsupportedWarning { get; }
    /// <summary>Returns the occurrence for a member ID, or null for a missing, invalid or foreign ID.</summary>
    public DxfEntity? FindEntity(DxfEntityId id) => byId.GetValueOrDefault(id);

    /// <summary>Loads and shapes text on an internal STA worker. The path stream is owned by this operation.</summary>
    public static Task<DxfLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return RunLoader(() => DxfReader.Load(path), cancellationToken);
    }

    /// <summary>Keep source alive and do not access it until completion. With leaveOpen=false it is disposed on every outcome.</summary>
    public static Task<DxfLoadResult> LoadAsync(Stream source, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RunLoader(() =>
        {
            try { return DxfReader.Load(source, true); }
            finally { if (!leaveOpen) source.Dispose(); }
        }, cancellationToken, () => { if (!leaveOpen) source.Dispose(); });
    }

    private static Task<DxfLoadResult> RunLoader(Func<DxfDocument> load, CancellationToken token, Action? preCancelled = null)
    {
        if (token.IsCancellationRequested)
        {
            preCancelled?.Invoke();
            return Task.FromCanceled<DxfLoadResult>(token);
        }
        var completion = new TaskCompletionSource<DxfLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            LoadingContext.Token = token;
            try
            {
                // Stream ownership is handled by the loader even if cancellation arrives before this worker starts.
                var document = load(); token.ThrowIfCancellationRequested();
                completion.TrySetResult(new(document, document.LoadDiagnostics));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { LoadingContext.Token = default; System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "VibeDxf document loader" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    /// <summary>Measures whole entities or eligible midpoints using prepared text/geometry. Visibility does not affect queries; incompatible coordinate spaces and invalid anchors return explicit statuses.</summary>
    public MeasurementResult Measure(MeasurementAnchor first, MeasurementAnchor second,
        MeasurementStrategy firstStrategy = MeasurementStrategy.Closest, MeasurementStrategy secondStrategy = MeasurementStrategy.Closest)
    {
        bool Default(DxfEntityId id) => id.DocumentId == Guid.Empty || id.Value <= 0;
        if (Default(first.EntityId) || Default(second.EntityId)) return new(MeasurementStatus.InvalidAnchor);
        if (first.EntityId.DocumentId != DocumentId || second.EntityId.DocumentId != DocumentId) return new(MeasurementStatus.ForeignDocument);
        var a = FindEntity(first.EntityId); var b = FindEntity(second.EntityId);
        if (a == null || b == null) return new(MeasurementStatus.EntityNotFound);
        if (!Enum.IsDefined(firstStrategy) || !Enum.IsDefined(secondStrategy) || !Valid(a, first.MidpointSegment) || !Valid(b, second.MidpointSegment))
            return new(MeasurementStatus.InvalidAnchor);
        if (a.CoordinateContext != b.CoordinateContext) return new(MeasurementStatus.CoordinateMismatch);
        if (a.Curves.Count == 0 || b.Curves.Count == 0) return new(MeasurementStatus.NoGeometry);
        var pair = new MeasurementTarget(a, first.MidpointSegment).Distance(new(b, second.MidpointSegment), firstStrategy, secondStrategy);
        return new(MeasurementStatus.Success, pair, a.IsApproximate || b.IsApproximate || a.Kind == "SPLINE" || b.Kind == "SPLINE", a.CoordinateContext);
    }

    private static bool Valid(DxfEntity e, int? midpoint) => midpoint == null ||
        (!e.IsAnnotation && !e.IsApproximate && e.Kind is not ("TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF" or "HATCH" or "SPLINE" or "POINT") &&
        midpoint >= 0 && midpoint < e.Curves.Count && e.Curves[midpoint.Value] is { IsLine: true, IsUnbounded: false } c && c.Start != c.End);
}

internal static class LoadingContext
{
    private static readonly AsyncLocal<CancellationToken> current = new();
    internal static CancellationToken Token { get => current.Value; set => current.Value = value; }
    internal static void Check() => Token.ThrowIfCancellationRequested();
}

/// <summary>An occurrence ID and optional exact eligible finite-segment midpoint index.</summary>
public readonly record struct MeasurementAnchor(DxfEntityId EntityId, int? MidpointSegment = null);
/// <summary>Outcome of a document-based measurement query.</summary>
public enum MeasurementStatus
{
    /// <summary>Distance and endpoints are available.</summary>
    Success,
    /// <summary>An anchor belongs to another document lineage.</summary>
    ForeignDocument,
    /// <summary>A valid-lineage occurrence ID is absent from this snapshot.</summary>
    EntityNotFound,
    /// <summary>An ID, midpoint or strategy is invalid.</summary>
    InvalidAnchor,
    /// <summary>Targets mix model and paper space or different paper layouts.</summary>
    CoordinateMismatch,
    /// <summary>At least one target has no usable prepared curves.</summary>
    NoGeometry
}
/// <summary>Distance-query outcome. Pair and coordinate context are present on success; failed queries contain no fabricated distance.</summary>
public sealed record MeasurementResult(MeasurementStatus Status, ClosestPair? Pair = null, bool IsApproximate = false,
    DxfCoordinateContext? CoordinateContext = null)
{
    /// <summary>Euclidean endpoint distance in drawing units, or null when the query did not succeed.</summary>
    public double? Distance => Pair?.Distance;
    /// <summary>Signed second-minus-first X displacement, or null for a failed query.</summary>
    public double? DeltaX => Pair is { } p ? p.B.X - p.A.X : null;
    /// <summary>Signed second-minus-first Y displacement, or null for a failed query.</summary>
    public double? DeltaY => Pair is { } p ? p.B.Y - p.A.Y : null;
}
