namespace VibeDxf;
// Transitional adapter; the parsing implementation lives only in Core.
internal static class DxfMLeader
{
 public static DxfEntity Read(List<(int Code,string Value)> tags,string layer,string handle,Action<string>? warn=null)=>
  CoreDocumentAdapter.Wpf(VibeDxf.Core.DxfMLeader.Read(tags,layer,handle,warn),warn)!;
}
