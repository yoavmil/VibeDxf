namespace VibeDxf;
// Transitional adapter; the parsing implementation lives only in Core.
internal static class DxfHatch
{
 public static DxfEntity Read(List<(int Code,string Value)> tags,string layer,string handle,Action<string>? warn=null)=>
  CoreDocumentAdapter.Wpf(VibeDxf.Core.DxfHatch.Read(tags,layer,handle),warn)!;
}
