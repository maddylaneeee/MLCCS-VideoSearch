using Microsoft.Data.Sqlite;

namespace MLCCS.VideoSearch.Core.Search;

public enum SearchSource { All, Filename, Visual, Speech, Ocr }
public sealed record SearchQuery(string Text, SearchSource Source = SearchSource.All, int Limit = 50, bool Phonetic = true);
public sealed record SearchResult(string Id, double Score, IReadOnlyList<RankContribution> Contributions, string Explanation);

public interface IVectorRecall
{
    Task<IReadOnlyList<RecallHit>> RecallAsync(string text, SearchSource source, int limit, CancellationToken cancellationToken);
}

public sealed class SearchCoordinator(string connectionString, IVectorRecall vectors)
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Text)) return [];
        var recalls = new List<RecallHit>();
        if (query.Source is SearchSource.All or SearchSource.Filename or SearchSource.Speech or SearchSource.Ocr)
            recalls.AddRange(await RecallFtsAsync(query, cancellationToken));
        if (query.Source is not SearchSource.Filename)
            recalls.AddRange(await vectors.RecallAsync(query.Text, query.Source, Math.Max(query.Limit, 50), cancellationToken));
        return RrfRanker.Fuse(recalls).Take(query.Limit).Select(hit => new SearchResult(hit.ResultId, hit.Score, hit.Contributions, Explain(hit.Contributions))).ToArray();
    }

    private async Task<IReadOnlyList<RecallHit>> RecallFtsAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString); await connection.OpenAsync(cancellationToken);
        var columns = query.Source switch { SearchSource.Filename => "filename", SearchSource.Speech => "transcript", SearchSource.Ocr => "ocr", _ => "search_fts" };
        var safeTokens = query.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token => '"' + token.Replace("\"", "\"\"") + '"');
        var match = string.Join(" AND ", safeTokens);
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT asset_id, bm25(search_fts), filename, transcript, ocr FROM search_fts WHERE {columns} MATCH $query ORDER BY rank LIMIT $limit";
        command.Parameters.AddWithValue("$query", match); command.Parameters.AddWithValue("$limit", Math.Max(query.Limit, 50));
        var hits = new List<RecallHit>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var rank = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            rank++; var source = query.Source == SearchSource.All ? "filename" : query.Source.ToString().ToLowerInvariant();
            var exact = Enumerable.Range(2, 3).Any(i => !reader.IsDBNull(i) && reader.GetString(i).Contains(query.Text, StringComparison.OrdinalIgnoreCase));
            hits.Add(new(reader.GetString(0), source, rank, -reader.GetDouble(1), exact));
        }
        return hits;
    }

    private static string Explain(IEnumerable<RankContribution> contributions) => string.Join("、", contributions.OrderByDescending(c => c.Value).Select(c => c.Phonetic ? "音近匹配" : c.Exact ? $"{c.Source} 精确匹配" : $"{c.Source} 相关"));
}
