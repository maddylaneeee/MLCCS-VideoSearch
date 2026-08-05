using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MLCCS.VideoSearch.UI.Services;

namespace MLCCS.VideoSearch.UI.Pages;

public sealed partial class JobsPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _refreshing;
    private string? _etaUpdatedUtc;
    private double _etaBaseSeconds;
    private DateTimeOffset _etaBaseUtc;

    public JobsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await RefreshStatusAsync();
        };
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var agent = await AgentClient.RequestAsync("agent.status");
            var configured = agent.GetProperty("configured").GetBoolean();
            var paused = agent.GetProperty("paused").GetBoolean();
            PauseButton.Content = paused ? "继续" : "暂停";
            if (agent.TryGetProperty("modelDownloadStatus", out var modelStatus) &&
                modelStatus.ValueKind == JsonValueKind.Object)
            {
                var modelStage = modelStatus.TryGetProperty("status", out var modelStageValue)
                    ? modelStageValue.GetString() : "Unknown";
                var modelProgress = modelStatus.TryGetProperty("progress", out var modelProgressValue)
                    ? modelProgressValue.GetDouble() : 0;
                var modelId = modelStatus.TryGetProperty("currentId", out var modelIdValue) &&
                              modelIdValue.ValueKind == JsonValueKind.String ? modelIdValue.GetString() : "—";
                ModelDownloadStatus.Text = $"模型下载：{modelStage} · {modelProgress:P1} · {modelId}";
            }
            if (!configured)
            {
                ErrorBar.IsOpen = false;
                StatusTitleText.Text = "等待添加资源库";
                StatusMessageText.Text = "请在资源库页添加文件夹；文件显示不依赖索引 Worker。";
                RealProgress.Value = 0;
                ProgressText.Text = "0.0%";
                return;
            }
            var running = agent.TryGetProperty("indexerRunning", out var runningNode) && runningNode.GetBoolean();
            var pending = agent.TryGetProperty("pendingLibraries", out var pendingNode) ? pendingNode.GetInt32() : 0;
            var scheduled = agent.TryGetProperty("scheduledLibraryChanges", out var scheduledNode)
                ? scheduledNode.GetInt32() : 0;
            if (!running && scheduled + pending > 0)
            {
                ErrorBar.IsOpen = false;
                StatusTitleText.Text = scheduled > 0 ? "检测到资源库变化" : "自动索引已排队";
                StatusMessageText.Text = scheduled > 0
                    ? $"正在合并文件变化，随后将增量索引 {scheduled:N0} 个资源库。"
                    : $"等待处理 {pending:N0} 个资源库。";
                return;
            }
            if (!agent.TryGetProperty("workerStatus", out var root) || root.ValueKind != JsonValueKind.Object)
            {
                ErrorBar.IsOpen = false;
                StatusTitleText.Text = running ? "正在启动索引 Worker" :
                    scheduled + pending > 0 ? "自动索引已排队" : "等待文件变化";
                StatusMessageText.Text = running
                    ? "资源库已配置，Worker 正在加载运行环境和模型。"
                    : scheduled + pending > 0 ? $"等待处理 {scheduled + pending:N0} 个资源库变化。"
                    : "自动索引已开启；检测到新增或修改文件后会在此显示。";
                return;
            }
            var stage = root.TryGetProperty("status", out var stageValue) && stageValue.ValueKind == JsonValueKind.String
                ? stageValue.GetString() ?? "Unknown" : "Unknown";
            var stageLabel = root.TryGetProperty("stageLabel", out var stageLabelValue) &&
                             stageLabelValue.ValueKind == JsonValueKind.String
                ? stageLabelValue.GetString() ?? stage : stage;
            var progress = root.TryGetProperty("progress", out var progressValue) && progressValue.ValueKind == JsonValueKind.Number
                ? Math.Clamp(progressValue.GetDouble(), 0, 1) : 0;
            var frames = root.TryGetProperty("segmentsIndexed", out var segmentsValue)
                ? segmentsValue.GetInt64() : 0;
            var rate = root.TryGetProperty("framesPerSecond", out var rateValue) && rateValue.ValueKind == JsonValueKind.Number
                ? rateValue.GetDouble() : 0;
            var workCompleted = root.TryGetProperty("workCompleted", out var workCompletedValue) && workCompletedValue.ValueKind == JsonValueKind.Number
                ? workCompletedValue.GetInt32() : completed;
            var workTotal = root.TryGetProperty("workTotal", out var workTotalValue) && workTotalValue.ValueKind == JsonValueKind.Number
                ? workTotalValue.GetInt32() : 0;
            var stageCompleted = root.TryGetProperty("stageCompleted", out var stageCompletedValue) && stageCompletedValue.ValueKind == JsonValueKind.Number
                ? stageCompletedValue.GetInt32() : 0;
            var stageTotal = root.TryGetProperty("stageTotal", out var stageTotalValue) && stageTotalValue.ValueKind == JsonValueKind.Number
                ? stageTotalValue.GetInt32() : 0;
            var current = root.TryGetProperty("currentFile", out var file) && file.ValueKind == JsonValueKind.String
                ? Path.GetFileName(file.GetString()) : "—";
            var completed = root.TryGetProperty("filesCompleted", out var completedValue) ? completedValue.GetInt32() : 0;
            var failed = root.TryGetProperty("filesFailed", out var failedValue) ? failedValue.GetInt32() : 0;
            var eta = root.TryGetProperty("etaSeconds", out var etaValue) ? etaValue.GetDouble() : 0;
            var updatedUtc = root.TryGetProperty("updatedUtc", out var updatedValue) &&
                             updatedValue.ValueKind == JsonValueKind.String
                ? updatedValue.GetString() : null;
            var activeStartedUtc = agent.TryGetProperty("activeIndexStartedUtc", out var activeNode) &&
                                   activeNode.ValueKind == JsonValueKind.String &&
                                   DateTimeOffset.TryParse(activeNode.GetString(), out var activeParsed)
                ? activeParsed : (DateTimeOffset?)null;
            var workerUpdatedUtc = DateTimeOffset.TryParse(updatedUtc, out var workerParsed)
                ? workerParsed : DateTimeOffset.MinValue;
            if (!string.Equals(updatedUtc, _etaUpdatedUtc, StringComparison.Ordinal))
            {
                _etaUpdatedUtc = updatedUtc;
                _etaBaseSeconds = eta;
                _etaBaseUtc = DateTimeOffset.TryParse(updatedUtc, out var parsed)
                    ? parsed : DateTimeOffset.UtcNow;
            }
            RealProgress.Value = progress * 100;
            ProgressText.Text = progress.ToString("P1");
            var staleWorkerStatus = running && activeStartedUtc is not null && workerUpdatedUtc < activeStartedUtc;
            StatusTitleText.Text = staleWorkerStatus ? "正在启动新一轮自动索引" :
                stage == "Completed" ? "索引已完成" :
                stage == "Paused" ? "索引已暂停" :
                stage == "Failed" ? "索引失败" : $"正在索引：{stageLabel}";
            StatusMessageText.Text = staleWorkerStatus
                ? "检测到资源库变化，Worker 正在加载；首份新状态写入后将显示实时进度。"
                : workTotal > 0
                    ? $"总进度 {progress:P1} · 当前 {stageLabel} {stageCompleted:N0}/{stageTotal:N0} · 已完成 {workCompleted:N0}/{workTotal:N0} 项"
                    : $"总进度 {progress:P1} · 已完成 {frames:N0} 个画面片段";
            ErrorBar.IsOpen = stage == "Failed" && !staleWorkerStatus;
            if (stage == "Failed" && !staleWorkerStatus)
            {
                ErrorBar.Title = "索引任务失败";
                ErrorBar.Message = root.TryGetProperty("error", out var errorNode) &&
                                   errorNode.ValueKind == JsonValueKind.String
                    ? errorNode.GetString() : "请打开状态目录查看日志。";
            }
            CompletedText.Text = workTotal > 0 ? $"{workCompleted:N0}/{workTotal:N0}" : completed.ToString("N0");
            FailedText.Text = failed.ToString("N0");
            var remaining = stage == "Paused" ? _etaBaseSeconds :
                Math.Max(0, _etaBaseSeconds - (DateTimeOffset.UtcNow - _etaBaseUtc).TotalSeconds);
            EtaText.Text = remaining > 0 && stage is not ("Completed" or "Failed" or "Cancelled")
                ? TimeSpan.FromSeconds(remaining).ToString(remaining >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
                : "—";
            CurrentFileStatus.Text = $"当前文件：{current}";
            var cuda = root.TryGetProperty("cuda", out var cudaValue) && cudaValue.ValueKind == JsonValueKind.String
                ? $"CUDA {cudaValue.GetString()}" : "CPU";
            var gpu = root.TryGetProperty("gpu", out var gpuValue) && gpuValue.ValueKind == JsonValueKind.String
                ? gpuValue.GetString() : "GPU 信息暂不可用";
            HardwareStatus.Text = $"设备：{gpu} · {cuda}";
            ThroughputStatus.Text = rate > 0.01
                ? $"实时处理速度：{rate:N1} 帧/秒"
                : "实时处理速度：正在采样";
        }
        catch (Exception error)
        {
            ErrorBar.IsOpen = true;
            ErrorBar.Title = "无法连接 Agent";
            ErrorBar.Message = error.Message;
        }
        finally { _refreshing = false; }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pause = PauseButton.Content?.ToString() != "继续";
            await AgentClient.RequestAsync(pause ? "index.pause" : "index.resume");
            await RefreshStatusAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "取消当前索引任务？",
            Content = "已提交的检查点和向量会保留。以后重新扫描会从可复用状态继续。",
            PrimaryButtonText = "取消任务",
            CloseButtonText = "返回",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await AgentClient.RequestAsync("index.cancel"); }
        catch (Exception error) { ShowError(error); }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        try { await AgentClient.RequestAsync("library.rescan"); }
        catch (Exception error) { ShowError(error); }
    }

    private void OpenStatus_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
    }

    private void ShowError(Exception error)
    {
        ErrorBar.IsOpen = true;
        ErrorBar.Title = "操作失败";
        ErrorBar.Message = error.Message;
    }
}
