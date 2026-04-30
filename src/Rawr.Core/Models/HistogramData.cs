namespace Rawr.Core.Models;

public sealed class HistogramData
{
    public int[] R { get; } = new int[256];
    public int[] G { get; } = new int[256];
    public int[] B { get; } = new int[256];
    public int[] Combined { get; } = new int[256]; // Rec.709 luminance
}
