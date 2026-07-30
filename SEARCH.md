# Search

## Recall sources

Search supports All, Filename, Visual, Speech and OCR modes. Single-source mode invokes only that source. All-mode concurrently obtains:

1. SQLite FTS5 filename exact/prefix recall.
2. OpenCLIP text-to-visual Qdrant Edge recall.
3. BGE query-to-transcript semantic recall.
4. OCR exact/FTS and semantic recall.
5. Optional pinyin/initial/fuzzy-phonetic recall.

Every returned segment retains asset ID, time bounds, thumbnail and source. Adjacent highly overlapping hits from one file may be grouped for display, but each timestamp remains expandable.

## Fusion and explanation

Ranking v1 uses Reciprocal Rank Fusion `weight / (60 + rank)`. Default weights are filename 1.20, visual 1.00, speech 1.00, OCR 0.90 and phonetic 0.45. Exact text receives a 1.5 multiplier. Each contribution is retained and transformed into a user explanation such as “filename 精确匹配” or “音近匹配”. Phonetic-only recall therefore cannot outrank a comparable exact match.

## Chinese phonetic assistance

At indexing time store normalized Chinese, full pinyin, initials and common fuzzy groups (`zh/z`, `ch/c`, `sh/s`, `l/n`, `ang/an`, `eng/en`). Query expansion uses the local corpus only, applies edit distance, and returns at most eight candidates. Off/low/medium/high settings change tolerance, never the eight-candidate cap. Candidates do not overwrite original Whisper text and are explicitly labeled.

## Search readiness and performance

During first indexing, the search box stays visible but disabled with a reason. It opens only after every initial asset is completed, skipped or user-confirmed failed. Filters cover library, format, duration, date and index status; sorting covers fused relevance, modification date and filename. First results should stream without reordering violently.

Windows acceptance targets on the fixed multilingual dataset: Recall@10 ≥ 0.80, MRR ≥ 0.65, phonetic Recall@10 improvement ≥ 20 percentage points on the proper-name subset, and warm p95 ≤ 1.5 seconds. Cold start is recorded separately.

