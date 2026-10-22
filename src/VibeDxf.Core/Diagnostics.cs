namespace VibeDxf.Core;
public enum DxfDiagnosticSeverity { Information, Warning }
public sealed record DxfDiagnostic(string Code,DxfDiagnosticSeverity Severity,string Phase,string? EntityKind,string Message,int Count,string? InstanceHandle=null);
public sealed record DxfLoadResult(DxfDocument Document,IReadOnlyList<DxfDiagnostic> Diagnostics);
