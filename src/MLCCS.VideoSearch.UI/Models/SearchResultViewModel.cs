using Microsoft.UI.Xaml.Media.Imaging;

namespace MLCCS.VideoSearch.UI.Models;

public sealed class SearchResultViewModel
{
    private BitmapImage? _thumbnail;
    private string? _loadedThumbnailPath;
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Library { get; set; } = "";
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public long TimestampMs { get; set; }
    public string Source { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string? ThumbnailPath { get; set; }
    public BitmapImage? Thumbnail
    {
        get
        {
            if (!string.Equals(_loadedThumbnailPath, ThumbnailPath, StringComparison.OrdinalIgnoreCase))
            {
                _loadedThumbnailPath = ThumbnailPath;
                if (File.Exists(ThumbnailPath))
                {
                    _thumbnail = new BitmapImage
                    {
                        DecodePixelWidth = 360,
                        UriSource = new Uri(ThumbnailPath!)
                    };
                }
                else _thumbnail = null;
            }
            return _thumbnail;
        }
    }
    public string TimestampText => TimeSpan.FromMilliseconds(TimestampMs).ToString(TimestampMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
    public string MetadataText => $"{TimestampText} · {Source} · {MediaItemViewModel.FormatBytes(SizeBytes)}";
}
