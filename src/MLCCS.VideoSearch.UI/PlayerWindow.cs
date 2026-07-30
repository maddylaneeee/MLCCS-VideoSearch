using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MLCCS.VideoSearch.UI;

internal sealed class PlayerWindow : Window
{
    private static readonly HashSet<PlayerWindow> OpenWindows = [];
    private readonly MediaPlayerElement _player;
    private readonly MediaPlayer _mediaPlayer;
    private readonly long _timestampMs;
    private bool _closed;

    private PlayerWindow(string path, long timestampMs)
    {
        _timestampMs = Math.Max(0, timestampMs);
        Title = $"播放 — {Path.GetFileName(path)}";
        SystemBackdrop = new MicaBackdrop();
        var title = new TextBlock
        {
            Text = $"{Path.GetFileName(path)}  ·  {TimeSpan.FromMilliseconds(_timestampMs):hh\\:mm\\:ss}",
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
            Margin = new Thickness(16, 12, 16, 8),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _player = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            AutoPlay = true,
            Stretch = Stretch.Uniform
        };
        _mediaPlayer = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), AutoPlay = true };
        _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        _player.SetMediaPlayer(_mediaPlayer);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(title);
        Grid.SetRow(_player, 1);
        grid.Children.Add(_player);
        Content = grid;
        Closed += PlayerWindow_Closed;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));
    }

    public static void Open(string path, long timestampMs)
    {
        var window = new PlayerWindow(path, timestampMs);
        OpenWindows.Add(window);
        window.Activate();
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        if (_closed) return;
        try
        {
            sender.PlaybackSession.Position = TimeSpan.FromMilliseconds(_timestampMs);
        }
        catch { }
    }

    private void PlayerWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true;
        Closed -= PlayerWindow_Closed;
        _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
        try { _mediaPlayer.Pause(); } catch { }
        try { _player.SetMediaPlayer(null); } catch { }
        Content = null;
        OpenWindows.Remove(this);

        // WinUI's transport controls release their COM references asynchronously.
        // Dispose on the next dispatcher turn after detaching the MediaPlayerElement.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                _mediaPlayer.Source = null;
                _mediaPlayer.Dispose();
            }
            catch { }
        });
    }
}
