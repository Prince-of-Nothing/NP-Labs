namespace Common;

/// <summary>One measurement row appended to the results CSV.</summary>
public class Metric
{
    public string Task = "";
    public int Threads;
    public double TimeMs;
    public double Speedup;
    public double PeakMB;
    public CounterResult Counts = new();
    public bool Correct;

    public string ToCsvRow() =>
        $"{Task},{Threads},{TimeMs:F0},{Speedup:F2},{PeakMB:F2}," +
        $"{Counts.Pos},{Counts.Neg},{Counts.Zero},{Counts.Total},{Correct}";

    public const string Header = "task,threads,time_ms,speedup,peak_mb,pos,neg,zero,total,correct";
}
