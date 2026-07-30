namespace MLCCS.VideoSearch.Core.Indexing;

public sealed record WhisperWord(string Text, long StartMs, long EndMs, double Probability);
public sealed record WhisperSegment(Guid Id, long StartMs, long EndMs, string Text, IReadOnlyList<WhisperWord> Words);
public sealed record SpeechSemanticWindow(Guid Id, long StartMs, long EndMs, string Text, IReadOnlyList<Guid> OriginalSegmentIds, int AlgorithmVersion = 1);

public static class SpeechWindowMerger
{
    public static IReadOnlyList<SpeechSemanticWindow> Merge(IEnumerable<WhisperSegment> source, long minimumMs = 8_000, long maximumMs = 30_000)
    {
        if (minimumMs <= 0 || maximumMs < minimumMs) throw new ArgumentOutOfRangeException(nameof(minimumMs));
        var segments = source.OrderBy(s => s.StartMs).ToArray();
        var result = new List<SpeechSemanticWindow>();
        var group = new List<WhisperSegment>();
        foreach (var segment in segments)
        {
            if (segment.EndMs < segment.StartMs) throw new ArgumentException("A Whisper segment has reversed timestamps.");
            var proposedDuration = group.Count == 0 ? segment.EndMs - segment.StartMs : segment.EndMs - group[0].StartMs;
            if (group.Count > 0 && proposedDuration > maximumMs)
            {
                result.Add(Create(group));
                group.Clear();
            }
            group.Add(segment);
            var duration = group[^1].EndMs - group[0].StartMs;
            var nextStartsAfterGap = segments.FirstOrDefault(s => s.StartMs > segment.StartMs)?.StartMs - segment.EndMs > 2_000;
            if (duration >= minimumMs && (duration >= maximumMs * 0.8 || nextStartsAfterGap))
            {
                result.Add(Create(group));
                group.Clear();
            }
        }
        if (group.Count > 0) result.Add(Create(group));
        return result;
    }

    public static WhisperSegment MostRelevantOriginal(SpeechSemanticWindow window, IEnumerable<WhisperSegment> originals, long hitMs) =>
        originals.Where(s => window.OriginalSegmentIds.Contains(s.Id))
            .OrderBy(s => DistanceToRange(hitMs, s.StartMs, s.EndMs)).First();

    private static SpeechSemanticWindow Create(IReadOnlyList<WhisperSegment> group) =>
        new(Guid.NewGuid(), group[0].StartMs, group[^1].EndMs,
            string.Join(' ', group.Select(s => s.Text.Trim()).Where(t => t.Length > 0)), group.Select(s => s.Id).ToArray());

    private static long DistanceToRange(long point, long start, long end) => point < start ? start - point : point > end ? point - end : 0;
}

