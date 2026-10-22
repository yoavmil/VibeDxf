namespace VibeDxf.Core;
/// <summary>Original ACI mode/index (0 BYBLOCK, 256 BYLAYER), optional true color and enclosing insertion color.</summary>
public sealed record DxfColorSource(int Index=256,int? TrueColor=null,uint? InheritedBlockColor=null);
