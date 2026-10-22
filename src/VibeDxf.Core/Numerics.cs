namespace VibeDxf.Core.Geometry;
public readonly record struct Point2D(double X,double Y)
{
 public static Point2D operator +(Point2D p,Vector2D v)=>new(p.X+v.X,p.Y+v.Y);
 public static Point2D operator -(Point2D p,Vector2D v)=>new(p.X-v.X,p.Y-v.Y);
 public static Vector2D operator -(Point2D a,Point2D b)=>new(a.X-b.X,a.Y-b.Y);
 public static explicit operator Vector2D(Point2D p)=>new(p.X,p.Y);
}
public record struct Vector2D(double X,double Y)
{
 public readonly double LengthSquared=>X*X+Y*Y;
 public readonly double Length=>Math.Sqrt(LengthSquared);
 public void Normalize(){var n=Length;if(!(n>0)||!double.IsFinite(n))throw new ArgumentException("Invalid vector.");X/=n;Y/=n;}
 public static Vector2D operator +(Vector2D a,Vector2D b)=>new(a.X+b.X,a.Y+b.Y);
 public static Vector2D operator -(Vector2D a,Vector2D b)=>new(a.X-b.X,a.Y-b.Y);
 public static Vector2D operator -(Vector2D a)=>new(-a.X,-a.Y);
 public static Vector2D operator *(Vector2D a,double b)=>new(a.X*b,a.Y*b);
 public static Vector2D operator *(double b,Vector2D a)=>a*b;
 public static Vector2D operator /(Vector2D a,double b)=>new(a.X/b,a.Y/b);
 public static double Multiply(Vector2D a,Vector2D b)=>a.X*b.X+a.Y*b.Y;
 public static double CrossProduct(Vector2D a,Vector2D b)=>a.X*b.Y-a.Y*b.X;
}
/// <summary>Finite numeric bounds. Empty differs from a zero-size point; Top/Bottom are min/max Y.</summary>
public record struct Bounds2D
{
 public double X{get;private set;} public double Y{get;private set;}
 public double Width{get;private set;} public double Height{get;private set;}
 public readonly bool IsEmpty=>Width<0;
 public readonly double Left=>X; public readonly double Top=>Y;
 public readonly double Right=>X+Width;public readonly double Bottom=>Y+Height;
 public readonly Point2D TopLeft=>new(Left,Top); public readonly Point2D TopRight=>new(Right,Top);
 public readonly Point2D BottomLeft=>new(Left,Bottom); public readonly Point2D BottomRight=>new(Right,Bottom);
 public static Bounds2D Empty=>new(){Width=-1,Height=-1};
 public Bounds2D(double x,double y,double width,double height){if(width<0||height<0)throw new ArgumentOutOfRangeException(nameof(width));X=x;Y=y;Width=width;Height=height;}
 public Bounds2D(Point2D a,Point2D b):this(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y)){}
 public void Union(Point2D p)=>Union(new Bounds2D(p,p));
 public void Union(Bounds2D b){if(b.IsEmpty)return;if(IsEmpty){this=b;return;}var r=Math.Max(Right,b.Right);var bottom=Math.Max(Bottom,b.Bottom);X=Math.Min(X,b.X);Y=Math.Min(Y,b.Y);Width=r-X;Height=bottom-Y;}
 public readonly bool Contains(Point2D p)=>!IsEmpty&&p.X>=Left&&p.X<=Right&&p.Y>=Top&&p.Y<=Bottom;
}
/// <summary>Double precision row-vector affine transform. Then(next) is next(this(point)). Rotate takes degrees.</summary>
public record struct Affine2D(double M11,double M12,double M21,double M22,double OffsetX,double OffsetY)
{
 public static Affine2D Identity=>new(1,0,0,1,0,0);
 public readonly double Determinant=>M11*M22-M12*M21;
 public readonly bool HasInverse=>double.IsFinite(Determinant)&&Determinant!=0;
 public readonly Point2D Transform(Point2D p)=>new(p.X*M11+p.Y*M21+OffsetX,p.X*M12+p.Y*M22+OffsetY);
 public readonly Vector2D Transform(Vector2D p)=>new(p.X*M11+p.Y*M21,p.X*M12+p.Y*M22);
 public readonly Affine2D Then(Affine2D b)=>new(M11*b.M11+M12*b.M21,M11*b.M12+M12*b.M22,M21*b.M11+M22*b.M21,M21*b.M12+M22*b.M22,OffsetX*b.M11+OffsetY*b.M21+b.OffsetX,OffsetX*b.M12+OffsetY*b.M22+b.OffsetY);
 public void Append(Affine2D b)=>this=Then(b);
 public void Translate(double x,double y)=>Append(new(1,0,0,1,x,y));
 public void Scale(double x,double y)=>Append(new(x,0,0,y,0,0));
 public void Rotate(double degrees){var a=degrees*Math.PI/180;var c=Math.Cos(a);var s=Math.Sin(a);Append(new(c,s,-s,c,0,0));}
 public void Invert(){var d=Determinant;if(!HasInverse)throw new InvalidOperationException("Singular transform.");this=new(M22/d,-M12/d,-M21/d,M11/d,(M21*OffsetY-OffsetX*M22)/d,(OffsetX*M12-M11*OffsetY)/d);}
}
