using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MLCCS.VideoSearch.UI.Models;
using MLCCS.VideoSearch.UI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace MLCCS.VideoSearch.UI.Pages;

public sealed partial class LibraryPage : Page
{
    public ObservableCollection<MediaItemViewModel> Items { get; } = [];
    public ObservableCollection<string> Libraries { get; } = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _refreshing;

    public LibraryPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
        var selectedPath = (AssetGrid.SelectedItem as MediaItemViewModel)?.Path
            ?? (AssetList.SelectedItem as MediaItemViewModel)?.Path;
        var settings = await AgentClient.RequestAsync("settings.get");
        var libraries = settings.GetProperty("libraries").EnumerateArray()
            .Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray();
        MergeLibraries(libraries);
        var items = await CatalogReader.ReadAssetsAsync(libraries);
        MergeItems(items);
        SummaryText.Text = Items.Count == 0 ? "尚无已发现的媒体文件" : $"{Items.Count:N0} 个媒体文件";
        if (selectedPath is not null)
        {
            var selected = Items.FirstOrDefault(item => item.Path == selectedPath);
            AssetGrid.SelectedItem = selected;
            AssetList.SelectedItem = selected;
        }
        await RefreshAgentStatusAsync();
        }
        catch (Exception error)
        {
            LibraryStatus.IsOpen = true;
            LibraryStatus.Title = "资源库刷新失败";
            LibraryStatus.Message = error.Message;
            LibraryStatus.Severity = InfoBarSeverity.Error;
        }
        finally { _refreshing = false; }
    }

    private void MergeLibraries(IReadOnlyList<string> libraries)
    {
        for (var index = 0; index < libraries.Count; index++)
        {
            if (index < Libraries.Count &&
                string.Equals(Libraries[index], libraries[index], StringComparison.OrdinalIgnoreCase))
                continue;
            var existing = Libraries.IndexOf(libraries[index]);
            if (existing >= 0) Libraries.Move(existing, index);
            else Libraries.Insert(index, libraries[index]);
        }
        while (Libraries.Count > libraries.Count) Libraries.RemoveAt(Libraries.Count - 1);
        LibrariesHeader.Text = $"已添加的文件夹（{Libraries.Count:N0}）";
    }

    private void MergeItems(IReadOnlyList<MediaItemViewModel> incoming)
    {
        for (var targetIndex = 0; targetIndex < incoming.Count; targetIndex++)
        {
            var item = incoming[targetIndex];
            var existingIndex = -1;
            for (var index = targetIndex; index < Items.Count; index++)
            {
                if (!string.Equals(Items[index].Path, item.Path, StringComparison.OrdinalIgnoreCase)) continue;
                existingIndex = index;
                break;
            }
            if (existingIndex < 0)
            {
                Items.Insert(targetIndex, item);
                continue;
            }
            if (existingIndex != targetIndex)
                Items.Move(existingIndex, targetIndex);
            if (!SamePresentation(Items[targetIndex], item))
                Items[targetIndex] = item;
        }
        while (Items.Count > incoming.Count)
            Items.RemoveAt(Items.Count - 1);
    }

    private static bool SamePresentation(MediaItemViewModel left, MediaItemViewModel right) =>
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) &&
        left.Name == right.Name && left.Library == right.Library && left.Extension == right.Extension &&
        left.SizeBytes == right.SizeBytes && left.DurationMs == right.DurationMs &&
        left.Status == right.Status && left.ThumbnailPath == right.ThumbnailPath;

    private async Task RefreshAgentStatusAsync()
    {
        try
        {
            var payload = await AgentClient.RequestAsync("agent.status");
            var configured = payload.GetProperty("configured").GetBoolean();
            if (!configured)
            {
                LibraryStatus.IsOpen = false;
                LibraryStateText.Text = "尚未添加资源库。选择“添加文件夹”后，文件会先显示，再在后台建立索引。";
                return;
            }
            if (payload.TryGetProperty("workerStatus", out var worker) && worker.ValueKind == JsonValueKind.Object)
            {
                var stage = worker.GetProperty("status").GetString() ?? "Unknown";
                var frames = worker.TryGetProperty("segmentsIndexed", out var segmentNode)
                    ? segmentNode.GetInt64() : 0;
                var progress = worker.GetProperty("progress").GetDouble();
                var running = payload.TryGetProperty("indexerRunning", out var runningNode) && runningNode.GetBoolean();
                var workerUpdated = worker.TryGetProperty("updatedUtc", out var updatedNode) &&
                                    updatedNode.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(updatedNode.GetString(), out var workerParsed)
                    ? workerParsed : DateTimeOffset.MinValue;
                var activeStarted = payload.TryGetProperty("activeIndexStartedUtc", out var activeNode) &&
                                    activeNode.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(activeNode.GetString(), out var activeParsed)
                    ? activeParsed : (DateTimeOffset?)null;
                var stale = running && activeStarted is not null && workerUpdated < activeStarted;
                if (stage == "Failed" && !stale)
                {
                    LibraryStatus.IsOpen = true;
                    LibraryStatus.Title = "索引遇到问题";
                    LibraryStatus.Message = worker.TryGetProperty("error", out var errorNode) &&
                                            errorNode.ValueKind == JsonValueKind.String
                        ? errorNode.GetString() : "请在索引任务页面查看详情。";
                    LibraryStatus.Severity = InfoBarSeverity.Error;
                }
                else LibraryStatus.IsOpen = false;
                LibraryStateText.Text = stale ? "检测到资源变化，正在启动新一轮自动索引；资源库可继续浏览。" :
                    stage is "Completed"
                    ? $"资源库已同步 · {Items.Count:N0} 个文件 · {frames:N0} 个画面片段"
                    : $"资源库可独立浏览 · 总进度 {progress:P1} · 已完成 {frames:N0} 个画面片段";
            }
            else
            {
                LibraryStatus.IsOpen = false;
                LibraryStateText.Text = "资源库已配置，正在扫描文件；索引 Worker 尚未开始。";
            }
        }
        catch (Exception error)
        {
            LibraryStatus.IsOpen = true;
            LibraryStatus.Title = "Agent 暂时不可用";
            LibraryStatus.Message = error.Message;
            LibraryStatus.Severity = InfoBarSeverity.Warning;
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerService.PickAsync();
        if (path is null) return;
        try
        {
            await AgentClient.RequestAsync("library.add", new { path });
            LibraryStatus.IsOpen = false;
            LibraryStateText.Text = $"已添加资源库：{path}";
            await RefreshAsync();
        }
        catch (Exception error)
        {
            LibraryStatus.Title = "无法添加资源库";
            LibraryStatus.Message = error.Message;
            LibraryStatus.Severity = InfoBarSeverity.Error;
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AgentClient.RequestAsync("library.rescan");
            LibraryStatus.IsOpen = false;
            LibraryStateText.Text = "已加入重新扫描队列；可复用索引会保留。";
        }
        catch (Exception error)
        {
            LibraryStatus.Title = "无法开始扫描";
            LibraryStatus.Message = error.Message;
            LibraryStatus.Severity = InfoBarSeverity.Error;
        }
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从资源库移除此文件夹？",
            Content = $"{path}\n\n只会移除资源库配置及对应索引，不会删除文件夹或其中的媒体文件。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await AgentClient.RequestAsync("library.remove", new { path });
            LibraryStatus.IsOpen = false;
            LibraryStateText.Text = $"已从资源库移除：{path}（磁盘文件未删除）";
            await RefreshAsync();
        }
        catch (Exception error)
        {
            LibraryStatus.IsOpen = true;
            LibraryStatus.Title = "无法移除资源库";
            LibraryStatus.Message = error.Message;
            LibraryStatus.Severity = InfoBarSeverity.Error;
        }
    }

    private void GridMode_Click(object sender, RoutedEventArgs e)
    {
        GridModeButton.IsChecked = true;
        ListModeButton.IsChecked = false;
        AssetGrid.Visibility = Visibility.Visible;
        AssetList.Visibility = Visibility.Collapsed;
    }

    private void ListMode_Click(object sender, RoutedEventArgs e)
    {
        ListModeButton.IsChecked = true;
        GridModeButton.IsChecked = false;
        AssetGrid.Visibility = Visibility.Collapsed;
        AssetList.Visibility = Visibility.Visible;
    }

    private async void Asset_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItemViewModel item) await ShowDetailsAsync(item);
    }

    private async void Asset_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MediaItemViewModel item ||
            item.ThumbnailPath is not null) return;
        await CatalogReader.EnsureThumbnailAsync(item);
    }

    private void Asset_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = (sender as ListViewBase)?.SelectedItem as MediaItemViewModel;
        if (item is not null) OpenPlayer(item);
    }

    private async Task ShowDetailsAsync(MediaItemViewModel item)
    {
        var content = new StackPanel { Spacing = 8, MinWidth = 520 };
        if (item.Thumbnail is not null)
            content.Children.Add(new Image { Source = item.Thumbnail, Height = 240, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
        content.Children.Add(new TextBlock { Text = item.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = $"{item.MetadataText} · {item.Status}" });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.Name,
            Content = content,
            PrimaryButtonText = "播放",
            SecondaryButtonText = "打开所在文件夹",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) OpenPlayer(item);
        else if (result == ContentDialogResult.Secondary) OpenContainingFolder(item.Path);
    }

    private static void OpenPlayer(MediaItemViewModel item)
    {
        if (!File.Exists(item.Path)) return;
        PlayerWindow.Open(item.Path, 0);
    }

    internal static void OpenContainingFolder(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add($"/select,{path}");
        System.Diagnostics.Process.Start(start);
    }

    internal static void CopyPath(string path)
    {
        var package = new DataPackage();
        package.SetText(path);
        Clipboard.SetContent(package);
    }
}
