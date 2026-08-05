using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MLCCS.VideoSearch.UI.Services;

namespace MLCCS.VideoSearch.UI.Pages;

public sealed class StorageCategoryRow
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public string SizeText => Models.MediaItemViewModel.FormatBytes(Bytes);
}

public sealed class StorageCleanupRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public string Reason { get; set; } = "";
    public string Detail => $"{Models.MediaItemViewModel.FormatBytes(Bytes)} · {Reason}";
}

public sealed partial class SettingsPage : Page
{
    private readonly ObservableCollection<string> _libraries = [];
    private readonly ObservableCollection<StorageCategoryRow> _storageCategories = [];
    private readonly ObservableCollection<StorageCleanupRow> _storageCandidates = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private string _speechModel = "whisper-medium";
    private bool _initialized;
    private bool _loading;
    private bool _saving;

    public SettingsPage()
    {
        InitializeComponent();
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveSettingsAsync();
        AttachImmediateSaveHandlers();
        _initialized = true;
        LibrariesList.ItemsSource = _libraries;
        StorageCategoryList.ItemsSource = _storageCategories;
        StorageCleanupList.ItemsSource = _storageCandidates;
        Loaded += async (_, _) => await LoadAsync();
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is applied while InitializeComponent is still constructing named panels.
        if (!_initialized) return;
        foreach (var panel in new FrameworkElement[]
                 { GeneralPanel, LibrariesPanel, IndexPanel, ModelsPanel, SearchPanel, StoragePanel, DiagnosticsPanel, AboutPanel })
            panel.Visibility = Visibility.Collapsed;
        var tag = (CategoryList.SelectedItem as ListViewItem)?.Tag?.ToString() ?? "general";
        (tag switch
        {
            "libraries" => LibrariesPanel,
            "index" => IndexPanel,
            "models" => ModelsPanel,
            "search" => SearchPanel,
            "storage" => StoragePanel,
            "diagnostics" => DiagnosticsPanel,
            "about" => AboutPanel,
            _ => GeneralPanel
        }).Visibility = Visibility.Visible;
        if (tag == "storage") _ = LoadStorageAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var settings = await AgentClient.RequestAsync("settings.get");
            _libraries.Clear();
            foreach (var library in settings.GetProperty("libraries").EnumerateArray())
                if (library.GetString() is { } path) _libraries.Add(path);
            AutoStartToggle.IsOn = settings.GetProperty("autoStart").GetBoolean();
            AutoIndexNewFilesToggle.IsOn = !settings.TryGetProperty("autoIndexNewFiles", out var autoIndexNode) ||
                                            autoIndexNode.GetBoolean();
            FilenameCheck.IsChecked = settings.GetProperty("filename").GetBoolean();
            VisualCheck.IsChecked = settings.GetProperty("visual").GetBoolean();
            SpeechCheck.IsChecked = settings.GetProperty("speech").GetBoolean();
            OcrCheck.IsChecked = settings.GetProperty("ocr").GetBoolean();
            _speechModel = settings.TryGetProperty("speechModel", out var modelNode)
                ? modelNode.GetString() ?? "whisper-medium"
                : "whisper-medium";
            SpeechEconomyRadio.IsChecked = _speechModel == "whisper-small";
            SpeechHighRadio.IsChecked = _speechModel == "whisper-large-v3";
            SpeechRecommendedRadio.IsChecked = _speechModel is not ("whisper-small" or "whisper-large-v3");
            AutomaticUpdatesToggle.IsOn = settings.GetProperty("automaticUpdates").GetBoolean();
            var idle = settings.TryGetProperty("searchModelIdleMinutes", out var idleNode) ? idleNode.GetInt32() : 10;
            Idle5Radio.IsChecked = idle == 5;
            Idle30Radio.IsChecked = idle == 30;
            Idle10Radio.IsChecked = idle is not (5 or 30);
            var phonetic = settings.GetProperty("phoneticExpansion").GetString();
            PhoneticOff.IsChecked = phonetic == "off";
            PhoneticLow.IsChecked = phonetic == "low";
            PhoneticHigh.IsChecked = phonetic == "high";
            PhoneticMedium.IsChecked = phonetic is not ("off" or "low" or "high");
            SaveStatus.Text = "";
        }
        catch (Exception error)
        {
            SaveStatus.Text = $"无法读取设置：{error.Message}";
        }
        finally { _loading = false; }
    }

    private void AttachImmediateSaveHandlers()
    {
        foreach (var toggle in new[]
                 { AutoStartToggle, AutoIndexNewFilesToggle, AutomaticUpdatesToggle })
            toggle.Toggled += SettingChanged;
        foreach (var check in new[] { FilenameCheck, VisualCheck, SpeechCheck, OcrCheck })
            check.Click += SettingChanged;
        foreach (var radio in new[]
                 {
                     SpeechEconomyRadio, SpeechRecommendedRadio, SpeechHighRadio,
                     PhoneticOff, PhoneticLow, PhoneticMedium, PhoneticHigh,
                     Idle5Radio, Idle10Radio, Idle30Radio
                 })
            radio.Checked += SettingChanged;
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !_initialized) return;
        SaveStatus.Text = "正在自动保存…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void AddLibrary_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerService.PickAsync();
        if (path is null) return;
        try
        {
            await AgentClient.RequestAsync("library.add", new { path });
            await LoadAsync();
        }
        catch (Exception error) { SaveStatus.Text = error.Message; }
    }

    private async void RemoveLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (LibrariesList.SelectedItem is not string path) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从资源库移除此文件夹？",
            Content = $"{path}\n\n只会移除资源库配置及对应索引，不会删除磁盘上的文件。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await AgentClient.RequestAsync("library.remove", new { path });
            await LoadAsync();
        }
        catch (Exception error) { SaveStatus.Text = error.Message; }
    }

    private void LibrariesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveLibraryButton.IsEnabled = LibrariesList.SelectedItem is string;

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await AgentClient.RequestAsync("library.rescan");
            SaveStatus.Text = "已加入扫描队列";
        }
        catch (Exception error) { SaveStatus.Text = error.Message; }
    }

    private async Task SaveSettingsAsync()
    {
        if (_loading || _saving) return;
        _saving = true;
        SaveStatus.Text = "正在保存…";
        try
        {
            var phonetic = PhoneticOff.IsChecked == true ? "off" :
                PhoneticLow.IsChecked == true ? "low" :
                PhoneticHigh.IsChecked == true ? "high" : "medium";
            _speechModel = SpeechEconomyRadio.IsChecked == true ? "whisper-small" :
                SpeechHighRadio.IsChecked == true ? "whisper-large-v3" : "whisper-medium";
            await AgentClient.RequestAsync("settings.update", new
            {
                libraries = _libraries,
                autoStart = AutoStartToggle.IsOn,
                autoIndexNewFiles = AutoIndexNewFilesToggle.IsOn,
                filename = true,
                visual = true,
                speech = SpeechCheck.IsChecked == true,
                speechModel = _speechModel,
                ocr = OcrCheck.IsChecked == true,
                automaticUpdates = AutomaticUpdatesToggle.IsOn,
                phoneticExpansion = phonetic,
                searchModelIdleMinutes = Idle5Radio.IsChecked == true ? 5 : Idle30Radio.IsChecked == true ? 30 : 10
            });
            SaveStatus.Text = "已保存";
        }
        catch (Exception error) { SaveStatus.Text = $"保存失败：{error.Message}"; }
        finally { _saving = false; }
    }

    private async Task LoadStorageAsync()
    {
        StorageStatusText.Text = "正在计算实际占用…";
        try
        {
            var summary = await AgentClient.RequestAsync("storage.summary");
            _storageCategories.Clear();
            foreach (var category in summary.GetProperty("categories").EnumerateArray())
            {
                _storageCategories.Add(new StorageCategoryRow
                {
                    Name = category.GetProperty("name").GetString() ?? "",
                    Bytes = category.GetProperty("bytes").GetInt64()
                });
            }
            _storageCandidates.Clear();
            foreach (var candidate in summary.GetProperty("candidates").EnumerateArray())
            {
                _storageCandidates.Add(new StorageCleanupRow
                {
                    Id = candidate.GetProperty("id").GetString() ?? "",
                    Name = candidate.GetProperty("name").GetString() ?? "",
                    Bytes = candidate.GetProperty("bytes").GetInt64(),
                    Reason = candidate.GetProperty("reason").GetString() ?? ""
                });
            }
            var total = summary.GetProperty("totalBytes").GetInt64();
            StorageTotalText.Text = $"应用相关占用：{Models.MediaItemViewModel.FormatBytes(total)}";
            StorageStatusText.Text = _storageCandidates.Count == 0 ? "当前没有可安全清理的未使用模型。" : "";
        }
        catch (Exception error)
        {
            StorageStatusText.Text = $"无法读取存储占用：{error.Message}";
        }
    }

    private async void RefreshStorage_Click(object sender, RoutedEventArgs e) => await LoadStorageAsync();

    private async void DeleteStorage_Click(object sender, RoutedEventArgs e)
    {
        var selected = StorageCleanupList.SelectedItems.OfType<StorageCleanupRow>().ToArray();
        if (selected.Length == 0)
        {
            StorageStatusText.Text = "请先选择要清理的模型。";
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除所选模型？",
            Content = $"将删除 {selected.Length:N0} 项，共 {Models.MediaItemViewModel.FormatBytes(selected.Sum(item => item.Bytes))}。以后需要时可以重新下载。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var result = await AgentClient.RequestAsync("storage.cleanup", new { ids = selected.Select(item => item.Id) });
            var reclaimed = result.GetProperty("reclaimedBytes").GetInt64();
            StorageStatusText.Text = $"已释放 {Models.MediaItemViewModel.FormatBytes(reclaimed)}。";
            await LoadStorageAsync();
        }
        catch (Exception error)
        {
            StorageStatusText.Text = $"清理失败：{error.Message}";
        }
    }

    private async void DownloadModels_Click(object sender, RoutedEventArgs e)
    {
        _speechModel = SpeechEconomyRadio.IsChecked == true ? "whisper-small" :
            SpeechHighRadio.IsChecked == true ? "whisper-large-v3" : "whisper-medium";
        var prefixes = new List<string>();
        if (SpeechCheck.IsChecked == true)
            prefixes.Add(_speechModel);
        if (OcrCheck.IsChecked == true)
        {
            prefixes.Add("ppocrv5-mobile-det");
            prefixes.Add("ppocrv5-mobile-rec");
        }
        if (prefixes.Count == 0)
        {
            ModelStatusText.Text = "请先在“索引内容”中启用语音或 OCR。";
            return;
        }

        ModelStatusText.Text = "已开始下载并校验；可在“任务”页面查看实时进度。";
        try
        {
            await AgentClient.RequestAsync("models.ensure", new { prefixes });
        }
        catch (Exception error)
        {
            ModelStatusText.Text = $"无法开始模型下载：{error.Message}";
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在验证稳定通道签名…";
        try
        {
            var result = await AgentClient.RequestAsync("update.check", new { interactive = true });
            UpdateStatusText.Text = result.GetProperty("message").GetString() ?? "检查完成";
            if (result.TryGetProperty("available", out var available) && available.GetBoolean())
            {
                var version = result.GetProperty("version").GetString()!;
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = $"版本 {version} 可用",
                    Content = result.GetProperty("releaseNotes").GetString(),
                    PrimaryButtonText = "立即更新", SecondaryButtonText = "稍后",
                    CloseButtonText = "跳过此版本", DefaultButton = ContentDialogButton.Primary
                };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.Primary)
                {
                    UpdateStatusText.Text = "正在下载并验证更新；完成后应用会请求安全退出。";
                    UpdateProgress.IsIndeterminate = false;
                    var apply = AgentClient.RequestAsync("update.apply");
                    while (!apply.IsCompleted)
                    {
                        await Task.Delay(500);
                        var status = await AgentClient.RequestAsync("update.status");
                        if (status.TryGetProperty("progress", out var progress))
                            UpdateProgress.Value = Math.Clamp(progress.GetDouble() * 100, 0, 100);
                    }
                    await apply;
                }
                else if (choice == ContentDialogResult.None)
                    await AgentClient.RequestAsync("update.skip", new { version });
            }
        }
        catch (Exception error) { UpdateStatusText.Text = $"检查更新失败：{error.Message}"; }
        finally { UpdateProgress.Visibility = Visibility.Collapsed; }
    }
}
