using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MLCCS.VideoSearch.UI.Models;

public sealed class MediaItemViewModel : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private string? _loadedThumbnailPath;
    private string? _thumbnailPath;
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Library { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string Status { get; set; } = "等待索引";
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (string.Equals(_thumbnailPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _thumbnailPath = value;
            _loadedThumbnailPath = null;
            _thumbnail = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Thumbnail));
        }
    }
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
    public string SizeText => FormatBytes(SizeBytes);
    public string DurationText => TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
    public string MetadataText => $"{DurationText} · {Extension.TrimStart('.').ToUpperInvariant()} · {SizeText}";
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
