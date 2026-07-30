using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MLCCS.VideoSearch.UI.Models;
using MLCCS.VideoSearch.UI.Services;

namespace MLCCS.VideoSearch.UI.Pages;

public sealed partial class SearchPage : Page
{
    public ObservableCollection<SearchResultViewModel> Results => SearchSessionState.Results;
    private readonly DispatcherTimer _readinessTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshing;
    private bool _searching;

    public SearchPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += async (_, _) =>
        {
            try
            {
                await RefreshLibrariesAsync();
            }
            catch (Exception error)
            {
                EnsureDefaultLibraryOption();
                SetStatus("资源文件夹列表暂不可用", error.Message,
                    InfoBarSeverity.Warning, remember: false);
            }
            RestoreSession();
            _readinessTimer.Start();
            await RefreshReadinessAsync();
        };
        Unloaded += (_, _) => _readinessTimer.Stop();
        _readinessTimer.Tick += async (_, _) => await RefreshReadinessAsync();
    }

    private async Task RefreshLibrariesAsync()
    {
        var settings = await AgentClient.RequestAsync("settings.get");
        var libraries = settings.GetProperty("libraries").EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = SearchSessionState.Library;
        EnsureDefaultLibraryOption();
        foreach (var library in libraries)
            LibrarySelector.Items.Add(new ComboBoxItem { Content = library, Tag = library });
        LibrarySelector.SelectedItem = LibrarySelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selected,
                StringComparison.OrdinalIgnoreCase)) ?? LibrarySelector.Items[0];
    }

    private void EnsureDefaultLibraryOption()
    {
        LibrarySelector.Items.Clear();
        LibrarySelector.Items.Add(new ComboBoxItem { Content = "全部资源文件夹", Tag = "" });
        LibrarySelector.SelectedIndex = 0;
    }

    private async Task RefreshReadinessAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await AgentClient.RequestAsync("agent.status");
            var configured = status.GetProperty("configured").GetBoolean();
            if (!configured)
            {
                QueryBox.IsEnabled = false;
                SetStatus("尚未添加资源库", "请先在资源库页添加媒体文件夹。",
                    InfoBarSeverity.Informational, remember: false);
                return;
            }
            var ready = false;
            var degraded = false;
            if (status.TryGetProperty("workerStatus", out var worker) && worker.ValueKind == JsonValueKind.Object)
            {
                var stage = worker.TryGetProperty("status", out var stageValue) ? stageValue.GetString() : "";
                var progress = worker.TryGetProperty("progress", out var progressValue)
                    ? progressValue.GetDouble() : 0;
                var frames = worker.TryGetProperty("framesIndexed", out var framesValue)
                    ? framesValue.GetInt64() : 0;
                // Existing filename/visual records remain queryable while an incremental run is active.
                ready = stage == "Completed" || frames > 0 || File.Exists(AppPaths.Database);
                degraded = ready && stage == "Failed";
                ready |= degraded;
            }
            QueryBox.IsEnabled = ready && !_searching;
            if (_searching) return;
            if (ready && SearchSessionState.HasCompletedSearch)
            {
                ApplySavedStatus();
            }
            else if (degraded)
            {
                SetStatus("视觉与文件名搜索可用",
                    "可选的语音/OCR 阶段失败；已完成的索引仍可搜索，请在索引任务页查看原因。",
                    InfoBarSeverity.Warning, remember: false);
            }
            else
            {
                SetStatus(ready ? "搜索已就绪" : "首次索引进行中",
                    ready ? "可搜索文件名、画面、语音和画面文字；结果预览对应命中时间点。"
                        : "资源库仍可浏览；视觉索引完成后搜索自动开放。",
                    ready ? InfoBarSeverity.Success : InfoBarSeverity.Informational, remember: false);
            }
        }
        catch (Exception error)
        {
            QueryBox.IsEnabled = false;
            SetStatus("Agent 暂时不可用", error.Message, InfoBarSeverity.Warning, remember: false);
        }
        finally { _refreshing = false; }
    }

    private async void QueryBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.QueryText.Trim();
        if (query.Length == 0) return;
        _searching = true;
        SearchSessionState.Query = query;
        SearchSessionState.Source = (SourceSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        SearchSessionState.Library = (LibrarySelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        SearchSessionState.HasCompletedSearch = false;
        QueryBox.IsEnabled = false;
        SetStatus("正在搜索", "首次查询会加载锁定模型，后续查询会复用常驻 Worker。",
            InfoBarSeverity.Informational, remember: false);
        Results.Clear();
        try
        {
            var source = SearchSessionState.Source;
            var library = SearchSessionState.Library;
            var response = await AgentClient.RequestAsync("search.query",
                new { query, source, library, limit = 60 });
            foreach (var result in response.GetProperty("results").EnumerateArray())
            {
                Results.Add(new SearchResultViewModel
                {
                    Path = result.GetProperty("path").GetString()!,
                    Name = result.GetProperty("name").GetString()!,
                    Library = result.TryGetProperty("library", out var resultLibrary)
                        ? resultLibrary.GetString() ?? "" : "",
                    SizeBytes = result.TryGetProperty("sizeBytes", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
                    DurationMs = result.TryGetProperty("durationMs", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetInt64() : 0,
                    TimestampMs = result.GetProperty("timestampMs").GetInt64(),
                    Source = result.GetProperty("source").GetString() ?? "",
                    Explanation = result.GetProperty("explanation").GetString() ?? "",
                    ThumbnailPath = result.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.String ? thumb.GetString() : null
                });
            }
            var elapsed = response.TryGetProperty("elapsedMs", out var elapsedValue) ? elapsedValue.GetDouble() : 0;
            var scope = string.IsNullOrEmpty(SearchSessionState.Library)
                ? "全部资源文件夹"
                : SearchSessionState.Library;
            SetStatus(Results.Count == 0 ? "没有结果" : $"找到 {Results.Count:N0} 个结果",
                $"范围：{scope} · 耗时 {elapsed:N0} 毫秒。预览图来自各结果的准确命中时间点。",
                Results.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, remember: true);
            SearchSessionState.HasCompletedSearch = true;
        }
        catch (Exception error)
        {
            SetStatus("搜索失败", error.Message, InfoBarSeverity.Error, remember: true);
            SearchSessionState.HasCompletedSearch = true;
        }
        finally
        {
            _searching = false;
            QueryBox.IsEnabled = true;
        }
    }

    private void ResultList_Click(object sender, RoutedEventArgs e)
    {
        ResultListButton.IsChecked = true;
        ResultGridButton.IsChecked = false;
        ResultList.Visibility = Visibility.Visible;
        ResultGrid.Visibility = Visibility.Collapsed;
        SearchSessionState.GridMode = false;
    }

    private void ResultGrid_Click(object sender, RoutedEventArgs e)
    {
        ResultGridButton.IsChecked = true;
        ResultListButton.IsChecked = false;
        ResultGrid.Visibility = Visibility.Visible;
        ResultList.Visibility = Visibility.Collapsed;
        SearchSessionState.GridMode = true;
    }

    private void RestoreSession()
    {
        QueryBox.Text = SearchSessionState.Query;
        foreach (var item in SourceSelector.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == SearchSessionState.Source)
            {
                SourceSelector.SelectedItem = item;
                break;
            }
        }
        foreach (var item in LibrarySelector.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), SearchSessionState.Library,
                    StringComparison.OrdinalIgnoreCase))
            {
                LibrarySelector.SelectedItem = item;
                break;
            }
        }
        if (SearchSessionState.GridMode) ResultGrid_Click(this, new RoutedEventArgs());
        else ResultList_Click(this, new RoutedEventArgs());
        if (SearchSessionState.HasCompletedSearch) ApplySavedStatus();
    }

    private void ApplySavedStatus()
    {
        SearchStateText.Text = $"{SearchSessionState.Title} · {SearchSessionState.Message}";
        SearchStatus.Title = SearchSessionState.Title;
        SearchStatus.Message = SearchSessionState.Message;
        SearchStatus.Severity = SearchSessionState.Severity;
        SearchStatus.IsOpen = SearchSessionState.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning;
    }

    private void SetStatus(string title, string message, InfoBarSeverity severity, bool remember)
    {
        SearchStateText.Text = $"{title} · {message}";
        if (SearchStatus.Title != title) SearchStatus.Title = title;
        if (SearchStatus.Message != message) SearchStatus.Message = message;
        if (SearchStatus.Severity != severity) SearchStatus.Severity = severity;
        SearchStatus.IsOpen = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning;
        if (!remember) return;
        SearchSessionState.Title = title;
        SearchSessionState.Message = message;
        SearchSessionState.Severity = severity;
    }

    private void Result_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultViewModel result) OpenPlayer(result);
    }

    private static void OpenPlayer(SearchResultViewModel result)
    {
        if (File.Exists(result.Path)) PlayerWindow.Open(result.Path, result.TimestampMs);
    }

    private void PlayResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchResultViewModel result) OpenPlayer(result);
    }

    private void FolderResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchResultViewModel result) LibraryPage.OpenContainingFolder(result.Path);
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SearchResultViewModel result) LibraryPage.CopyPath(result.Path);
    }
}
