namespace MLCCS.VideoSearch.Core.Indexing;

public sealed record SceneSample(long TimestampMs, double DifferenceScore, double MotionScore = 0);
public sealed record VisualSegment(long StartMs, long EndMs, IReadOnlyList<long> RepresentativeMs, int AlgorithmVersion = 1);

public static class SceneSegmenter
{
    public const double DefaultThreshold = 27;
    public const long MinimumMs = 2_000;
    public const long MaximumMs = 8_000;

    public static IReadOnlyList<VisualSegment> Segment(
        long durationMs,
        IEnumerable<SceneSample> samples,
        double threshold = DefaultThreshold,
        double highMotionThreshold = 35)
    {
        if (durationMs <= 0) return [];
        var ordered = samples.Where(s => s.TimestampMs > 0 && s.TimestampMs < durationMs)
            .OrderBy(s => s.TimestampMs).ToArray();
        var cuts = new List<long> { 0 };
        foreach (var sample in ordered.Where(s => s.DifferenceScore >= threshold))
        {
            if (sample.TimestampMs - cuts[^1] >= MinimumMs)
                cuts.Add(sample.TimestampMs);
        }
        if (durationMs - cuts[^1] < MinimumMs && cuts.Count > 1) cuts.RemoveAt(cuts.Count - 1);
        cuts.Add(durationMs);

        var result = new List<VisualSegment>();
        for (var i = 0; i < cuts.Count - 1; i++)
        {
            var sceneStart = cuts[i];
            var sceneEnd = cuts[i + 1];
            var windowStart = sceneStart;
            while (windowStart < sceneEnd)
            {
                var windowEnd = Math.Min(sceneEnd, windowStart + MaximumMs);
                if (sceneEnd - windowEnd is > 0 and < MinimumMs)
                    windowEnd = sceneEnd;
                AddWindow(result, ordered, windowStart, windowEnd, highMotionThreshold);
                windowStart = windowEnd;
            }
        }
        return result;
    }

    private static void AddWindow(List<VisualSegment> output, SceneSample[] samples, long start, long end, double motionThreshold)
    {
        var midpoint = start + ((end - start) / 2);
        var representatives = new List<long> { midpoint };
        var peak = samples.Where(s => s.TimestampMs >= start && s.TimestampMs <= end)
            .OrderByDescending(s => s.MotionScore).FirstOrDefault();
        if (peak is not null && peak.MotionScore >= motionThreshold && Math.Abs(peak.TimestampMs - midpoint) >= 500)
            representatives.Add(peak.TimestampMs);
        output.Add(new VisualSegment(start, end, representatives));
    }
}

