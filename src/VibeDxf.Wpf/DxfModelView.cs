using System.Windows;
using System.Windows.Media.Media3D;

namespace VibeDxf.Wpf;

/// <summary>Identity of a prepared model-space camera in a document lineage.</summary>
public readonly record struct DxfModelViewId(Guid DocumentId, int Value);
/// <summary>Where the saved model camera definition was stored.</summary>
public enum DxfModelViewSource
{
    /// <summary>An active or named VPORT table configuration.</summary>
    Viewport,
    /// <summary>A named VIEW table entry.</summary>
    NamedView,
    /// <summary>Legacy header viewing variables.</summary>
    Header
}
/// <summary>An immutable saved camera. Coordinates refer to WCS; framing is in the camera display coordinate system.</summary>
public sealed class DxfModelView
{
    internal DxfModelView(string name,DxfModelViewSource source,Vector3D direction,Point3D target,Point center,double height,double aspectRatio,
        double twist=0,bool perspective=false,double lensLength=50,bool frontClipping=false,bool backClipping=false,bool frontAtEye=true,double frontClip=0,double backClip=0)
    {
        Name=name;Source=source;Direction=direction;Target=target;Center=center;Height=height;AspectRatio=aspectRatio;Twist=twist;
        Perspective=perspective;LensLength=lensLength;FrontClipping=frontClipping;BackClipping=backClipping;FrontAtEye=frontAtEye;FrontClip=frontClip;BackClip=backClip;
    }
    /// <summary>Document-local identity; foreign view identities are rejected by controls.</summary>
    public DxfModelViewId Id {get;internal set;}
    /// <summary>Saved view or configuration name; active tiled windows have numbered suffixes.</summary>
    public string Name {get;}
    /// <summary>The source record category.</summary>
    public DxfModelViewSource Source {get;}
    /// <summary>Vector from target toward the eye. Its magnitude is retained for perspective projection.</summary>
    public Vector3D Direction {get;}
    /// <summary>World-coordinate viewing target.</summary>
    public Point3D Target {get;}
    /// <summary>Saved center in display-coordinate-system drawing units.</summary>
    public Point Center {get;}
    /// <summary>Saved view height in drawing units.</summary>
    public double Height {get;}
    /// <summary>Saved width divided by height.</summary>
    public double AspectRatio {get;}
    /// <summary>Saved view twist in degrees.</summary>
    public double Twist {get;}
    /// <summary>Whether this camera uses perspective projection.</summary>
    public bool Perspective {get;}
    /// <summary>Saved lens length in millimetres.</summary>
    public double LensLength {get;}
    /// <summary>Whether front clipping is enabled.</summary>
    public bool FrontClipping {get;}
    /// <summary>Whether back clipping is enabled.</summary>
    public bool BackClipping {get;}
    /// <summary>Whether front clipping uses the eye plane rather than the stored front offset.</summary>
    public bool FrontAtEye {get;}
    /// <summary>Front plane offset along view direction from the target.</summary>
    public double FrontClip {get;}
    /// <summary>Back plane offset along view direction from the target.</summary>
    public double BackClip {get;}
    /// <summary>Whether XY anchors can represent this view without losing source Z.</summary>
    public bool IsInteractive => !Perspective&&!FrontClipping&&!BackClipping&&Math.Abs(Direction.X)<1e-9&&Math.Abs(Direction.Y)<1e-9&&Direction.Z>0;
    /// <summary>Whether this camera was prepared successfully for display.</summary>
    public bool IsSupported => Window!=null;
    internal DxfViewportWindow? Window {get;set;}
    internal Rect Frame => new(Center.X-Height*AspectRatio/2,Center.Y-Height/2,Height*AspectRatio,Height);
}
