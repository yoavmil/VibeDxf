using System.Globalization;
using System.Runtime.ExceptionServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

[CollectionDefinition("Visual checks")]
public sealed class VisualChecksCollection : ICollectionFixture<ReportFixture> { }

public sealed class ReportFixture : IDisposable
{
    private readonly IDisposable report = VisualReport.Start(captureConsole: false);
    public void Dispose() => report.Dispose();
}

internal static class TestSupport
{
    public static void Run(string name, Action test)
    {
        try { test(); VisualReport.RecordResult(name, true); }
        catch (Exception ex) { VisualReport.RecordResult(name, false, ex.ToString()); throw; }
    }
    public static void Near(double expected, double actual, double tolerance = 1e-8) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected:R}; actual {actual:R}; tolerance {tolerance:R}");
    public static void Sta(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try { test(); } catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

internal sealed record CheckCase(string Name, bool Passed);

