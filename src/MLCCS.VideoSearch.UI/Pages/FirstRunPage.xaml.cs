using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MLCCS.VideoSearch.UI.Services;

namespace MLCCS.VideoSearch.UI.Pages;

public sealed partial class FirstRunPage : Page
{
    private string? _libraryPath;
    private bool _cudaAvailable;
    private bool _hardwareSupported;
    private string _speechModel = "whisper-medium";

    public FirstRunPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await DetectHardwareAsync();
    }

    private async Task DetectHardwareAsync()
    {
        try
        {
            var hardware = await AgentClient.RequestAsync("hardware.detect");
            _cudaAvailable = hardware.GetProperty("cuda_available").GetBoolean();
            _hardwareSupported = hardware.GetProperty("supported").GetBoolean();
            var cpu = hardware.GetProperty("cpu").GetString();
            var logical = hardware.GetProperty("logical_processors").GetInt32();
            var memory = hardware.GetProperty("memory_bytes").GetInt64();
            var gpu = hardware.TryGetProperty("gpu_name", out var gpuValue) && gpuValue.ValueKind == JsonValueKind.String
                ? gpuValue.GetString() : "未检测到 CUDA GPU";
            var vram = hardware.GetProperty("vram_bytes").GetInt64();
            var windows = hardware.GetProperty("windows_version").GetString() ?? "未知 Windows 版本";
            var driver = hardware.TryGetProperty("driver_version", out var driverNode) &&
                         driverNode.ValueKind == JsonValueKind.String ? driverNode.GetString() : "未检测到";
            var cudaVersion = hardware.TryGetProperty("cuda_version", out var cudaNode) &&
                              cudaNode.ValueKind == JsonValueKind.String ? cudaNode.GetString() : "不可用";
            HardwareText.Text = $"Windows {windows} · {cpu} · {logical} 个逻辑处理器 · {MLCCS.VideoSearch.UI.Models.MediaItemViewModel.FormatBytes(memory)} 内存\n" +
                                $"{gpu} · {MLCCS.VideoSearch.UI.Models.MediaItemViewModel.FormatBytes(vram)} 显存 · NVIDIA 驱动 {driver} · PyTorch CUDA {cudaVersion}（{(_cudaAvailable ? "可用" : "不可用")}）";
            var recommendation = hardware.GetProperty("whisperRecommendation");
            _speechModel = $"whisper-{recommendation.GetProperty("model").GetString()}";
            RecommendationText.Text = _hardwareSupported
                ? $"语音模型建议：{recommendation.GetProperty("tier").GetString()}（{recommendation.GetProperty("model").GetString()} / {recommendation.GetProperty("compute_type").GetString()}）"
                : $"此机器不满足 v1.0.0 要求，索引和搜索已阻止：{string.Join("；", hardware.GetProperty("support_issues").EnumerateArray().Select(item => item.GetString()))}。请更新 Windows/NVIDIA 驱动；无需另装 CUDA Toolkit。";
            SpeechCheck.IsEnabled = _hardwareSupported;
            OcrCheck.IsEnabled = _hardwareSupported;
            SpeechCheck.IsChecked = _hardwareSupported;
        }
        catch (Exception error)
        {
            HardwareText.Text = $"硬件检测失败：{error.Message}";
            RecommendationText.Text = "仍可配置资源库，但语音索引保持关闭。";
            SpeechCheck.IsEnabled = false;
            OcrCheck.IsEnabled = false;
        }
        finally
        {
            HardwareProgress.IsActive = false;
            HardwareProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void ChooseLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryPath = await FolderPickerService.PickAsync();
        if (_libraryPath is null) return;
        LibraryPathText.Text = _libraryPath;
        StartButton.IsEnabled = _hardwareSupported;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryPath is null) return;
        StartButton.IsEnabled = false;
        SetupStatus.IsOpen = true;
        SetupStatus.Title = "正在保存设置";
        SetupStatus.Message = "Agent 将验证资源库并启动真实索引。";
        SetupStatus.Severity = InfoBarSeverity.Informational;
        try
        {
            await AgentClient.RequestAsync("settings.update", new
            {
                libraries = new[] { _libraryPath },
                autoStart = true,
                autoIndexNewFiles = true,
                intervalSeconds = 4.0,
                batchSize = 0,
                decoderWorkers = 0,
                resourcePolicy = "adaptive-full",
                filename = true,
                visual = true,
                speech = _cudaAvailable && SpeechCheck.IsChecked == true,
                ocr = OcrCheck.IsChecked == true,
                speechModel = _speechModel,
                automaticUpdates = true,
                searchModelIdleMinutes = 10,
                phoneticExpansion = "medium"
            });
            var prefixes = new List<string>();
            if (_cudaAvailable && SpeechCheck.IsChecked == true) prefixes.Add(_speechModel);
            if (OcrCheck.IsChecked == true)
            {
                prefixes.Add("ppocrv5-mobile-det");
                prefixes.Add("ppocrv5-mobile-rec");
            }
            if (prefixes.Count > 0)
                await AgentClient.RequestAsync("models.ensure", new { prefixes });
            await AgentClient.RequestAsync("library.rescan");
            SetupStatus.Title = "设置完成";
            SetupStatus.Message = "真实索引已加入 Agent 队列。";
            SetupStatus.Severity = InfoBarSeverity.Success;
            App.CurrentWindow?.NavigateToLibrary();
        }
        catch (Exception error)
        {
            SetupStatus.Title = "无法开始索引";
            SetupStatus.Message = error.Message;
            SetupStatus.Severity = InfoBarSeverity.Error;
            StartButton.IsEnabled = true;
        }
    }
}
