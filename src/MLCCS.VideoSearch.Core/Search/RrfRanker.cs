namespace MLCCS.VideoSearch.Core.Search;

public sealed record RecallHit(string ResultId, string Source, int Rank, double SourceScore, bool Exact = false, bool Phonetic = false);
public sealed record RankContribution(string Source, int Rank, double Value, bool Exact, bool Phonetic);
public sealed record FusedHit(string ResultId, double Score, IReadOnlyList<RankContribution> Contributions);

public static class RrfRanker
{
    public const int DefaultK = 60;
    private static readonly IReadOnlyDictionary<string, double> DefaultWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["filename"] = 1.20, ["visual"] = 1.00, ["speech"] = 1.00, ["ocr"] = 0.90, ["phonetic"] = 0.45
    };

    public static IReadOnlyList<FusedHit> Fuse(IEnumerable<RecallHit> hits, IReadOnlyDictionary<string, double>? weights = null, int k = DefaultK)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        weights ??= DefaultWeights;
        return hits.GroupBy(h => h.ResultId, StringComparer.Ordinal)
            .Select(group =>
            {
                var contributions = group.Select(h =>
                {
                    var weight = weights.TryGetValue(h.Phonetic ? "phonetic" : h.Source, out var value) ? value : 1;
                    var exactBoost = h.Exact ? 1.5 : 1;
                    var contribution = weight * exactBoost / (k + Math.Max(1, h.Rank));
                    return new RankContribution(h.Source, h.Rank, contribution, h.Exact, h.Phonetic);
                }).ToArray();
                return new FusedHit(group.Key, contributions.Sum(c => c.Value), contributions);
            })
            .OrderByDescending(h => h.Score).ThenBy(h => h.ResultId, StringComparer.Ordinal).ToArray();
    }
}

