using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using MLCCS.VideoSearch.Core.Updates;
using MLCCS.VideoSearch.Core.Privacy;

namespace MLCCS.VideoSearch.OnlineInstaller;

internal static class Program
{
    internal const string ProductName = "MLCCS VideoSearch";
    internal const string Version = "1.0.0";
    internal const string ManifestUrl =
        "https://lixinchen.ca/docs/mlccs-video-search/1.0.0/release-manifest.json";
    internal const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MLCCSVideoSearch";

    [STAThread]
    private static void Main(string[] args)
    {
        StartupDiagnostics.Initialize(args);
        try
        {
            var prerequisiteReportArgument = args.FirstOrDefault(argument =>
                argument.StartsWith("--prerequisite-report=", StringComparison.OrdinalIgnoreCase));
            if (prerequisiteReportArgument is not null)
            {
                var outputPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                    prerequisiteReportArgument[(prerequisiteReportArgument.IndexOf('=') + 1)..]));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(
                    SystemPrerequisites.Inspect(),
                    new JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
            {
                StartupDiagnostics.ReportFatal("Windows UI thread", eventArgs.Exception);
                Application.Exit();
            };
            ApplicationConfiguration.Initialize();
            StartupDiagnostics.Write("WinForms initialization completed.");

            var invokedAsUninstaller = string.Equals(
                Path.GetFileName(Environment.ProcessPath),
                "uninstaller.exe",
                StringComparison.OrdinalIgnoreCase);
            if (invokedAsUninstaller ||
                args.Length > 0 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall(args.Any(argument =>
                    argument.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));
                return;
            }

            if (args.Length > 1 && args[0].Equals("--uninstall-final", StringComparison.OrdinalIgnoreCase))
            {
                FinalizeUninstall(args[1], args.Any(argument =>
                    argument.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));
                return;
            }

            if (args.Any(argument => argument.Equals("--quiet", StringComparison.OrdinalIgnoreCase)))
            {
                SilentInstaller.RunAsync(args).GetAwaiter().GetResult();
                return;
            }

            StartupDiagnostics.Write("Creating installer window.");
            using var form = new InstallerForm();
            StartupDiagnostics.Write("Installer window created; entering message loop.");
            Application.Run(form);
            StartupDiagnostics.Write("Installer exited normally.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.ReportFatal("Installer startup", ex);
        }
    }

    private static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show(
                $"是否卸载 {ProductName}？\n\n索引、设置和下载的模型会保留在当前用户的应用数据目录中。",
                "卸载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var installDirectory = uninstallKey?.GetValue("InstallLocation") as string;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            var executableDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            installDirectory = string.Equals(
                    Path.GetFileName(executableDirectory),
                    "_installer",
                    StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(executableDirectory)?.FullName ?? executableDirectory
                : executableDirectory;
        }
        installDirectory = Path.GetFullPath(installDirectory);
        foreach (var processName in new[] { "MLCCS.VideoSearch.UI", "MLCCS.VideoSearch.Agent", "MLCCS.VideoSearch.Updater" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                    // The final removal still reports a useful error if files remain locked.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        DeleteShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{ProductName}.lnk"));
        DeleteShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "MLCCS",
            $"{ProductName}.lnk"));
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        var temporaryCopy = Path.Combine(
            Path.GetTempPath(),
            $"MLCCS-VideoSearch-Uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(Environment.ProcessPath!, temporaryCopy, overwrite: true);
        Process.Start(new ProcessStartInfo
        {
            FileName = temporaryCopy,
            Arguments = $"--uninstall-final \"{installDirectory}\"{(quiet ? " --quiet" : string.Empty)}",
            UseShellExecute = true
        });
    }

    private static void FinalizeUninstall(string installDirectory, bool quiet)
    {
        Thread.Sleep(1500);
        try
        {
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }
            if (!quiet)
            {
                MessageBox.Show("卸载完成。用户设置、索引和按需模型未被删除。", "卸载",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show($"未能完全删除安装目录：\n{installDirectory}\n\n{ex.Message}",
                    "卸载未完全完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                StartupDiagnostics.Write($"Quiet uninstall could not remove '{installDirectory}': {ex}");
            }
        }
    }

    internal static void DeleteShortcut(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Shortcut cleanup is best effort.
        }
    }
}

internal sealed class InstallerForm : Form
{
    private readonly TextBox _installPath = new();
    private readonly CheckedListBox _components = new();
    private readonly CheckBox _desktopShortcut = new();
    private readonly Label _prerequisiteSummary = new();
    private readonly Label _downloadSummary = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _installButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _browseButton = new();
    private ReleaseManifest? _manifest;
    private CancellationTokenSource? _installCancellation;
    private PauseController? _pauseController;
    private bool _deleteDownloadsOnCancellation;
    private bool _closeAfterCancellation;
    private bool _installationActive;

    internal InstallerForm()
    {
        StartupDiagnostics.Write("InstallerForm constructor started.");
        Text = $"{Program.ProductName} 安装程序";
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            Icon = Icon.ExtractAssociatedIcon(processPath);
        }
        Width = 720;
        Height = 650;
        MinimumSize = new Size(680, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label
        {
            Text = "安装 MLCCS VideoSearch",
            Font = new Font("Segoe UI Semibold", 18),
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 0, 8)
        };
        var explanation = new Label
        {
            Text = "支持暂停、断线重试和跨重启续传；大型模型可预下载，也可在首次使用时按需下载。",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 0, 0, 16)
        };

        var pathLabel = new Label { Text = "安装位置", AutoSize = true, Dock = DockStyle.Top };
        _installPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "MLCCS VideoSearch");
        _installPath.Dock = DockStyle.Fill;
        _browseButton.Text = "浏览…";
        _browseButton.AutoSize = true;
        _browseButton.Click += BrowseClick;
        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            ColumnCount = 2,
            Padding = new Padding(0, 4, 0, 0)
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(_installPath, 0, 0);
        pathRow.Controls.Add(_browseButton, 1, 0);

        var componentLabel = new Label
        {
            Text = "下载内容",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 12, 0, 4)
        };
        _components.Dock = DockStyle.Fill;
        _components.CheckOnClick = true;
        _components.ItemCheck += (_, eventArgs) =>
        {
            if (_manifest is not null &&
                eventArgs.Index >= 0 &&
                _manifest.Components[eventArgs.Index].Required &&
                eventArgs.NewValue != CheckState.Checked)
            {
                eventArgs.NewValue = CheckState.Checked;
            }
            BeginInvoke(UpdateDownloadSummary);
        };
        _downloadSummary.AutoSize = true;
        _downloadSummary.Dock = DockStyle.Top;
        _downloadSummary.ForeColor = SystemColors.GrayText;
        _downloadSummary.Padding = new Padding(0, 6, 0, 8);

        _desktopShortcut.Text = "在桌面创建快捷方式";
        _desktopShortcut.Checked = true;
        _desktopShortcut.AutoSize = true;
        _desktopShortcut.Dock = DockStyle.Top;
        _desktopShortcut.Padding = new Padding(0, 6, 0, 10);

        _prerequisiteSummary.AutoSize = true;
        _prerequisiteSummary.Dock = DockStyle.Top;
        _prerequisiteSummary.ForeColor = SystemColors.GrayText;
        _prerequisiteSummary.Padding = new Padding(0, 0, 0, 8);
        RefreshPrerequisiteSummary();

        _status.Text = "正在读取安装清单…";
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Top;
        _status.Height = 28;
        _progress.Dock = DockStyle.Top;
        _progress.Height = 22;
        _progress.Maximum = 1000;

        _installButton.Text = "安装";
        _installButton.Enabled = false;
        _installButton.AutoSize = true;
        _installButton.Padding = new Padding(18, 4, 18, 4);
        _installButton.Click += InstallClick;
        _pauseButton.Text = "暂停";
        _pauseButton.Enabled = false;
        _pauseButton.AutoSize = true;
        _pauseButton.Padding = new Padding(12, 4, 12, 4);
        _pauseButton.Click += PauseClick;
        _cancelButton.Text = "取消";
        _cancelButton.Enabled = false;
        _cancelButton.AutoSize = true;
        _cancelButton.Padding = new Padding(12, 4, 12, 4);
        _cancelButton.Click += CancelClick;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        actions.Controls.Add(_installButton);
        actions.Controls.Add(_pauseButton);
        actions.Controls.Add(_cancelButton);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 11,
            ColumnCount = 1,
            Padding = new Padding(28)
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(title);
        content.Controls.Add(explanation);
        content.Controls.Add(pathLabel);
        content.Controls.Add(pathRow);
        content.Controls.Add(componentLabel);
        content.Controls.Add(_components);
        content.Controls.Add(_downloadSummary);
        content.Controls.Add(_desktopShortcut);
        content.Controls.Add(_prerequisiteSummary);
        content.Controls.Add(_status);
        content.Controls.Add(_progress);
        Controls.Add(content);
        Controls.Add(actions);

        Shown += async (_, _) => await LoadManifestAsync();
        FormClosing += InstallerFormClosing;
        StartupDiagnostics.Write("InstallerForm constructor completed.");
    }

    private async Task LoadManifestAsync()
    {
        StartupDiagnostics.Write($"Loading installer manifest: {Program.ManifestUrl}");
        try
        {
            using var http = CreateHttpClient();
            var json = await http.GetStringAsync(Program.ManifestUrl);
            _manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("安装清单为空。");
            using (var key = EmbeddedUpdateKey.Create())
            {
                if (!UpdateVerifier.VerifyManifest(_manifest, key))
                    throw new CryptographicException("安装清单签名无效；不会信任其中的版本、网址或哈希。");
            }
            if (!_manifest.ProductVersion.Equals(Program.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"安装清单版本 {_manifest.ProductVersion} 与安装器版本 {Program.Version} 不一致。");
            }

            _components.Items.Clear();
            foreach (var component in _manifest.Components)
            {
                var label = $"{component.Name} {(component.Required ? "（必需）" : "（可选）")} — {component.Description} — {FormatBytes(component.Size)}";
                var index = _components.Items.Add(label,
                    component.Required || component.DefaultSelected);
                if (component.Required)
                {
                    _components.SetItemCheckState(index, CheckState.Checked);
                }
            }
            _status.Text = "已就绪。必需组件提供视觉与文本检索；语音和 OCR 按安装选择启用。";
            _installButton.Enabled = true;
            UpdateDownloadSummary();
            StartupDiagnostics.Write("Installer manifest loaded successfully.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"Manifest load failed: {ex}");
            _status.Text = "无法读取在线安装清单。";
            MessageBox.Show($"无法读取安装清单：\n{ex.Message}", "安装器",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshPrerequisiteSummary()
    {
        var report = SystemPrerequisites.Inspect();
        _prerequisiteSummary.Text = report.HasRepairableIssues
            ? "系统依赖：检测到缺失项；点击“安装”后将从 Microsoft/Windows Update 下载并补齐。"
            : "系统依赖：已就绪（运行时与 Windows 媒体/核心组件已复检）。";
        _prerequisiteSummary.ForeColor = report.HasRepairableIssues
            ? Color.DarkGoldenrod : Color.DarkGreen;
    }

    internal static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(45),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MLCCS-VideoSearch-Installer", Program.Version));
        return http;
    }

    private void BrowseClick(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 MLCCS VideoSearch 安装目录",
            UseDescriptionForTitle = true,
            SelectedPath = _installPath.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installPath.Text = Path.Combine(dialog.SelectedPath, "MLCCS VideoSearch");
        }
    }

    private void UpdateDownloadSummary()
    {
        if (_manifest is null)
        {
            return;
        }
        var bytes = SelectedComponents().Sum(component => component.Size);
        _downloadSummary.Text =
            $"预计下载 {FormatBytes(bytes)}；进度会持久保存，安装并验证完成后删除缓存包。";
    }

    private IReadOnlyList<ReleaseComponent> SelectedComponents()
    {
        if (_manifest is null)
        {
            return Array.Empty<ReleaseComponent>();
        }
        return _manifest.Components.Where((component, index) =>
            component.Required || _components.GetItemChecked(index)).ToArray();
    }

    private void PauseClick(object? sender, EventArgs e)
    {
        if (!_installationActive || _pauseController is null)
        {
            return;
        }

        if (_pauseController.IsPaused)
        {
            _pauseController.Resume();
            _pauseButton.Text = "暂停";
            _status.Text = "正在恢复下载或安装…";
            StartupDiagnostics.Write("Installation resumed by user.");
        }
        else
        {
            _pauseController.Pause();
            _pauseButton.Text = "继续";
            _status.Text = "已暂停。下载进度已保留，可点击“继续”。";
            StartupDiagnostics.Write("Installation paused by user.");
        }
    }

    private void CancelClick(object? sender, EventArgs e)
    {
        if (!_installationActive || _installCancellation is null)
        {
            return;
        }

        if (MessageBox.Show(
                "是否取消安装？\n\n取消会删除本次已下载和未完成的安装包；已经解压到安装目录的文件不会被静默删除。",
                "取消安装",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _deleteDownloadsOnCancellation = true;
        _status.Text = "正在取消并删除下载缓存…";
        _pauseController?.Resume();
        _installCancellation.Cancel();
        StartupDiagnostics.Write("Installation cancellation requested; cached downloads will be deleted.");
    }

    private void InstallerFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_installationActive || _installCancellation is null)
        {
            return;
        }

        e.Cancel = true;
        if (MessageBox.Show(
                "退出安装器并保留下载进度吗？\n\n下次启动后将从已下载位置继续。",
                "退出安装器",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _deleteDownloadsOnCancellation = false;
        _closeAfterCancellation = true;
        _status.Text = "正在安全停止并保留下载进度…";
        _pauseController?.Resume();
        _installCancellation.Cancel();
        StartupDiagnostics.Write("Installer close requested; cached download progress will be preserved.");
    }

    private async void InstallClick(object? sender, EventArgs e)
    {
        if (_manifest is null)
        {
            return;
        }
        var installDirectory = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(_installPath.Text.Trim()));
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            MessageBox.Show("请选择安装位置。", "安装器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selected = SelectedComponents();
        _installCancellation = new CancellationTokenSource();
        _pauseController = new PauseController();
        _deleteDownloadsOnCancellation = false;
        _closeAfterCancellation = false;
        _installationActive = true;
        ToggleControls(false);
        var cancellationToken = _installCancellation.Token;
        var downloadDirectory = GetDownloadCacheDirectory();
        try
        {
            var prerequisites = SystemPrerequisites.Inspect();
            if (!prerequisites.CanInstall)
            {
                throw new PlatformNotSupportedException(prerequisites.Describe());
            }
            if (prerequisites.HasRepairableIssues)
            {
                if (MessageBox.Show(
                        "安装器检测到缺失或过旧的 Windows 基础依赖。\n\n" +
                        prerequisites.Describe() +
                        "\n\n继续后只会从 Microsoft 官方入口和 Windows Update 下载，并验证安装包签名。可能显示 UAC 提示，修复系统组件可能需要重启。是否继续？",
                        "需要补齐系统依赖", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information) != DialogResult.Yes)
                {
                    return;
                }
                using var prerequisiteHttp = CreateHttpClient();
                await SystemPrerequisites.EnsureAsync(
                    prerequisiteHttp,
                    Path.Combine(GetDownloadCacheDirectory(), "prerequisites"),
                    message => _status.Text = message,
                    cancellationToken);
                RefreshPrerequisiteSummary();
            }
            if (!FeedbackState.ResetIfNeeded(quiet: false, explicitReset: false)) return;
            Directory.CreateDirectory(installDirectory);
            Directory.CreateDirectory(downloadDirectory);
            long completedBytes = 0;
            var totalBytes = Math.Max(1, selected.Sum(component => component.Size));
            using var http = CreateHttpClient();

            foreach (var component in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _pauseController.WaitAsync(cancellationToken);
                var archivePath = Path.Combine(downloadDirectory, Path.GetFileName(component.Url.LocalPath));
                _status.Text = $"正在下载：{component.Name}";
                await DownloadAsync(
                    http,
                    component.Url.AbsoluteUri,
                    archivePath,
                    component.Size,
                    _pauseController,
                    cancellationToken,
                    (downloaded, detail) =>
                    {
                        var aggregate = completedBytes + downloaded;
                        _progress.Value = (int)Math.Clamp(aggregate * 1000 / totalBytes, 0, 1000);
                        _status.Text = string.IsNullOrWhiteSpace(detail)
                            ? $"正在下载：{component.Name}  {FormatBytes(downloaded)} / {FormatBytes(component.Size)}"
                            : $"{component.Name}：{detail}";
                    });

                _status.Text = $"正在后台校验下载包：{component.Name}";
                var archiveHash = await ComputeSha256Async(
                    archivePath, _pauseController, cancellationToken);
                if (!archiveHash.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteIfExists(archivePath);
                    throw new InvalidDataException(
                        $"{component.Name} SHA-256 不匹配。损坏的下载包已删除，请重试。");
                }

                var destination = ComponentDestination(installDirectory, component);
                Directory.CreateDirectory(destination);
                _status.Text = $"正在解压：{component.Name}";
                await ExtractArchiveAsync(
                    archivePath,
                    destination,
                    _pauseController,
                    cancellationToken,
                    new Progress<ItemProgress>(progress => _status.Text =
                        $"正在解压：{component.Name}  {progress.Completed} / {progress.Total} 个文件"));

                _status.Text = $"正在后台验证已安装文件：{component.Name}";
                await VerifyFilesAsync(
                    destination,
                    component.Files,
                    _pauseController,
                    cancellationToken,
                    new Progress<ItemProgress>(progress => _status.Text =
                        $"正在验证：{component.Name}  {progress.Completed} / {progress.Total} 个文件"));
                DeleteIfExists(archivePath);
                completedBytes += component.Size;
            }

            await FinalizeInstallAsync(installDirectory, selected);
            _progress.Value = 1000;
            _status.Text = "安装完成，所有已安装文件均已通过完整性验证。";
            _installationActive = false;
            if (MessageBox.Show("安装完成。是否现在启动 MLCCS VideoSearch？", "安装器",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(installDirectory, _manifest.EntryPoint),
                    WorkingDirectory = installDirectory,
                    UseShellExecute = true
                });
            }
            Close();
        }
        catch (OperationCanceledException)
        {
            if (_deleteDownloadsOnCancellation)
            {
                DeleteCachedDownloads(selected);
                _status.Text = "安装已取消，已下载和未完成的安装包已删除。";
            }
            else
            {
                _status.Text = "安装已停止，下载进度已保留供下次继续。";
            }
            StartupDiagnostics.Write(_deleteDownloadsOnCancellation
                ? "Installation cancelled; cached downloads deleted."
                : "Installation stopped; cached downloads preserved.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"Installation failed: {ex}");
            _status.Text = "安装未完成，下载进度已保留。";
            MessageBox.Show($"安装失败：\n{ex.Message}\n\n下载进度已保留；重新运行安装器可继续。已安装目录不会被静默删除。",
                "安装器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _installationActive = false;
            _pauseController?.Resume();
            _pauseController = null;
            _installCancellation?.Dispose();
            _installCancellation = null;
            ToggleControls(true);
            if (_closeAfterCancellation)
            {
                BeginInvoke(Close);
            }
        }
    }

    internal static async Task DownloadAsync(
        HttpClient http,
        string url,
        string outputPath,
        long expectedSize,
        PauseController pauseController,
        CancellationToken cancellationToken,
        Action<long, string?> progress)
    {
        var partialPath = outputPath + ".partial";
        if (File.Exists(outputPath))
        {
            var completeSize = new FileInfo(outputPath).Length;
            if (completeSize == expectedSize)
            {
                progress(completeSize, "已从持久化缓存恢复完整下载包");
                return;
            }
            DeleteIfExists(outputPath);
        }

        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existing > expectedSize)
        {
            DeleteIfExists(partialPath);
            existing = 0;
        }
        if (existing > 0)
        {
            progress(existing, $"已恢复 {FormatBytes(existing)}，继续断点下载");
        }

        const long minimumSegmentSize = 2L * 1024 * 1024;
        const long initialSegmentSize = 8L * 1024 * 1024;
        const long maximumSegmentSize = 64L * 1024 * 1024;
        var segmentSize = initialSegmentSize;
        var consecutiveFailures = 0;

        while (existing < expectedSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pauseController.WaitAsync(cancellationToken);
            var segmentEnd = Math.Min(expectedSize - 1, existing + segmentSize - 1);
            var requestedBytes = segmentEnd - existing + 1;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(existing, segmentEnd);
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.NotFound or
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    throw new HttpRequestException(
                        $"下载服务器返回不可重试状态 {(int)response.StatusCode} {response.ReasonPhrase}",
                        null,
                        response.StatusCode);
                }
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (existing > 0)
                    {
                        DeleteIfExists(partialPath);
                    }
                    existing = 0;
                    requestedBytes = expectedSize;
                    progress(0, "当前网络节点不支持 Range，将使用兼容的连续下载");
                }
                else if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new HttpRequestException(
                        $"下载服务器返回状态 {(int)response.StatusCode} {response.ReasonPhrase}",
                        null,
                        response.StatusCode);
                }
                response.EnsureSuccessStatusCode();
                var contentRange = response.Content.Headers.ContentRange;
                if (response.StatusCode == HttpStatusCode.PartialContent &&
                    contentRange?.From != existing)
                {
                    throw new InvalidDataException(
                        $"下载分段起点不匹配：请求 {existing}，响应 {contentRange?.From?.ToString() ?? "未知"}。");
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partialPath,
                    existing > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[1024 * 1024];
                var total = existing;
                var remaining = requestedBytes;
                var lastUpdate = Stopwatch.StartNew();
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pauseController.WaitAsync(cancellationToken);
                    using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    idleCancellation.CancelAfter(TimeSpan.FromMinutes(2));
                    var read = await input.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        idleCancellation.Token);
                    if (read == 0)
                    {
                        throw new IOException("下载连接在当前分段完成前结束。");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    remaining -= read;
                    existing = total;
                    if (lastUpdate.ElapsedMilliseconds >= 150)
                    {
                        progress(total, null);
                        lastUpdate.Restart();
                    }
                }
                await output.FlushAsync(cancellationToken);
                progress(existing, null);
                consecutiveFailures = 0;
                segmentSize = Math.Min(maximumSegmentSize, segmentSize * 2);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryableDownloadFailure(ex))
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 100)
                {
                    throw new IOException("下载连续失败 100 次，请检查网络、代理或服务器状态。", ex);
                }
                existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                segmentSize = Math.Max(minimumSegmentSize, segmentSize / 2);
                var waitSeconds = Math.Min(60, Math.Pow(2, Math.Min(6, consecutiveFailures)));
                progress(existing,
                    $"网络中断，{waitSeconds:0} 秒后从 {FormatBytes(existing)} 继续（第 {consecutiveFailures} 次重试）");
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            }
        }

        File.Move(partialPath, outputPath, overwrite: true);
        progress(expectedSize, "下载完成，准备后台校验");
    }

    internal static async Task VerifyFilesAsync(
        string destination,
        IReadOnlyList<ReleaseFile> files,
        PauseController pauseController,
        CancellationToken cancellationToken,
        IProgress<ItemProgress> progress)
    {
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pauseController.WaitAsync(cancellationToken);
            var file = files[index];
            var fullPath = Path.GetFullPath(Path.Combine(
                destination, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
            {
                throw new InvalidDataException($"安装文件缺失：{file.Path}");
            }
            var info = new FileInfo(fullPath);
            if (info.Length != file.Size)
            {
                throw new InvalidDataException($"安装文件大小不匹配：{file.Path}");
            }
            var hash = await ComputeSha256Async(
                fullPath, pauseController, cancellationToken);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装文件校验失败：{file.Path}");
            }
            progress.Report(new ItemProgress(index + 1, files.Count));
        }
    }

    internal static Task ExtractArchiveAsync(
        string archivePath,
        string destination,
        PauseController pauseController,
        CancellationToken cancellationToken,
        IProgress<ItemProgress> progress)
    {
        return Task.Run(async () =>
        {
            var destinationRoot = Path.GetFullPath(destination)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(archivePath);
            var files = archive.Entries.Where(entry =>
                !string.IsNullOrEmpty(entry.Name)).ToArray();
            var completed = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseController.WaitAsync(cancellationToken);
                var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var outputPath = Path.GetFullPath(Path.Combine(destination, relativePath));
                if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"压缩包包含不安全路径：{entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await pauseController.WaitAsync(cancellationToken);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                completed++;
                progress.Report(new ItemProgress(completed, files.Length));
            }
        }, cancellationToken);
    }

    private async Task FinalizeInstallAsync(
        string installDirectory,
        IReadOnlyList<ReleaseComponent> selected)
    {
        if (_manifest is null)
        {
            throw new InvalidOperationException("安装清单未加载。");
        }
        var installerDirectory = Path.Combine(installDirectory, "_installer");
        Directory.CreateDirectory(installerDirectory);
        Directory.CreateDirectory(Path.Combine(installerDirectory, "downloads"));
        var uninstaller = Path.Combine(installDirectory, "uninstaller.exe");
        File.Copy(Environment.ProcessPath!, uninstaller, overwrite: true);
        var packagedUpdater = Path.Combine(installDirectory, "current", "updater", "MLCCS.VideoSearch.Updater.exe");
        if (!File.Exists(packagedUpdater)) throw new FileNotFoundException("安装包缺少更新器。", packagedUpdater);
        File.Copy(packagedUpdater, Path.Combine(installerDirectory, "MLCCS.VideoSearch.Updater.exe"), overwrite: true);

        var record = new
        {
            installedUtc = DateTimeOffset.UtcNow,
            version = _manifest.ProductVersion,
            manifestUrl = Program.ManifestUrl,
            components = selected.Select(component => new
            {
                component.Id,
                component.Name,
                component.Sha256
            })
        };
        await File.WriteAllTextAsync(Path.Combine(installerDirectory, "installed.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        FeedbackState.MarkV1Installed();

        var entryPoint = Path.Combine(installDirectory, _manifest.EntryPoint);
        var startMenuShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "MLCCS", $"{Program.ProductName}.lnk");
        Shortcut.Create(startMenuShortcut, entryPoint, installDirectory,
            "本地视频语义搜索");
        if (_desktopShortcut.Checked)
        {
            Shortcut.Create(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"{Program.ProductName}.lnk"), entryPoint, installDirectory,
                "本地视频语义搜索");
        }

        using var key = Registry.CurrentUser.CreateSubKey(Program.UninstallKey);
        key.SetValue("DisplayName", Program.ProductName);
        key.SetValue("DisplayVersion", Program.Version);
        key.SetValue("Publisher", "MLCCS");
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("DisplayIcon", entryPoint);
        key.SetValue("UninstallString", $"\"{uninstaller}\"");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize",
            (int)Math.Min(int.MaxValue,
                Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length) / 1024),
            RegistryValueKind.DWord);
    }

    private void ToggleControls(bool enabled)
    {
        _installButton.Enabled = enabled;
        _browseButton.Enabled = enabled;
        _installPath.Enabled = enabled;
        _components.Enabled = enabled;
        _desktopShortcut.Enabled = enabled;
        _pauseButton.Enabled = !enabled;
        _cancelButton.Enabled = !enabled;
        if (enabled)
        {
            _pauseButton.Text = "暂停";
        }
    }

    internal static Task<string> ComputeSha256Async(
        string path,
        PauseController pauseController,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4 * 1024 * 1024,
                FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[4 * 1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await pauseController.WaitAsync(cancellationToken);
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }, cancellationToken);
    }

    private static string GetDownloadCacheDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MLCCS", "VideoSearch", "Installer", "downloads", Program.Version);
    }

    private static void DeleteCachedDownloads(IReadOnlyList<ReleaseComponent> components)
    {
        var directory = GetDownloadCacheDirectory();
        foreach (var component in components)
        {
            var archivePath = Path.Combine(directory, Path.GetFileName(component.Url.LocalPath));
            DeleteIfExists(archivePath);
            DeleteIfExists(archivePath + ".partial");
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write($"Could not delete cache file '{path}': {ex.Message}");
        }
    }

    private static bool IsRetryableDownloadFailure(Exception exception)
    {
        if (exception is HttpRequestException requestException &&
            requestException.StatusCode is HttpStatusCode.NotFound or
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return false;
        }

        if (exception is IOException ioException &&
            (ioException.HResult & 0xffff) is 0x27 or 0x70)
        {
            return false;
        }

        return exception is HttpRequestException or IOException or TaskCanceledException;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    internal static string ComponentDestination(string installDirectory, ReleaseComponent component)
    {
        if (component.InstallScope.Equals("current", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(installDirectory, "current");
        var scope = component.InstallScope switch
        {
            "runtime" => "runtime", "qdrant" => "qdrant", "visual-model" => "visual-model",
            "text-model" => "text-model", "ocr-models" => "ocr-models",
            _ => throw new InvalidDataException($"未知安装范围：{component.InstallScope}")
        };
        return Path.Combine(installDirectory, "components", scope, component.Sha256);
    }


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

internal static class FeedbackState
{
    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLCCS", "VideoSearch");

    internal static void MarkV1Installed()
    {
        Directory.CreateDirectory(StateRoot);
        File.WriteAllText(Path.Combine(StateRoot, "v1-state.marker"), $"1.0.0 {DateTimeOffset.UtcNow:O}");
    }

    internal static bool ResetIfNeeded(bool quiet, bool explicitReset)
    {
        var stateRoot = StateRoot;
        var marker = Path.Combine(stateRoot, "v1-state.marker");
        var detected = !File.Exists(marker) && (File.Exists(Path.Combine(stateRoot, "libraries.json")) ||
                                                File.Exists(Path.Combine(stateRoot, "catalog.db")) ||
                                                Directory.Exists(Path.Combine(stateRoot, "models")));
        if (!detected) return true;
        if (quiet && !explicitReset)
            throw new InvalidOperationException("FEEDBACK_RESET_REQUIRED: rerun with --reset-feedback; source videos are never deleted.");
        if (!quiet && MessageBox.Show(
                "检测到 Feedback 版配置、索引或模型缓存。v1.0.0 需要完整重新开始。\n\n继续将删除这些应用数据，但绝不会删除任何原始视频。是否继续？",
                "需要重置 Feedback 数据", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;
        foreach (var file in new[] { "libraries.json", "catalog.db", "catalog.db-wal", "catalog.db-shm",
                     "real-index-status.json", "real-index.lock", "real-index.pause", "real-index.cancel" })
        {
            var path = Path.Combine(stateRoot, file);
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var directory in new[] { "models", "thumbnails", "qdrant" })
        {
            var path = Path.Combine(stateRoot, directory);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(stateRoot);
        MarkV1Installed();
        return true;
    }
}

internal static class SilentInstaller
{
    internal static async Task RunAsync(string[] args)
    {
        using var http = InstallerForm.CreateHttpClient();
        await SystemPrerequisites.EnsureAsync(
            http,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MLCCS", "VideoSearch", "Installer", "downloads", Program.Version, "prerequisites"),
            StartupDiagnostics.Write,
            CancellationToken.None);
        var reset = args.Any(item => item.Equals("--reset-feedback", StringComparison.OrdinalIgnoreCase));
        FeedbackState.ResetIfNeeded(quiet: true, explicitReset: reset);
        var installRootArgument = args.FirstOrDefault(item => item.StartsWith("--install-root=", StringComparison.OrdinalIgnoreCase));
        var installRoot = Path.GetFullPath(installRootArgument is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MLCCS VideoSearch")
            : Environment.ExpandEnvironmentVariables(installRootArgument[(installRootArgument.IndexOf('=') + 1)..]));
        var json = await http.GetStringAsync(Program.ManifestUrl);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, options)
                       ?? throw new InvalidDataException("安装清单为空。");
        using (var verificationKey = EmbeddedUpdateKey.Create())
            if (!UpdateVerifier.VerifyManifest(manifest, verificationKey))
                throw new CryptographicException("安装清单签名无效。");
        if (manifest.ProductVersion != Program.Version) throw new InvalidDataException("安装清单版本不匹配。");
        var selected = manifest.Components.Where(item => item.Required || item.DefaultSelected).ToArray();
        var requiredBytes = selected.Sum(item => item.Size);
        var drive = new DriveInfo(Path.GetPathRoot(installRoot)!);
        if (drive.AvailableFreeSpace < requiredBytes * 2 + 1024L * 1024 * 1024)
            throw new IOException("安装空间不足。");
        Directory.CreateDirectory(installRoot);
        var installerRoot = Path.Combine(installRoot, "_installer");
        var downloads = Path.Combine(installerRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var pause = new PauseController();
        foreach (var component in selected)
        {
            var archive = Path.Combine(downloads, Path.GetFileName(component.Url.LocalPath));
            await InstallerForm.DownloadAsync(http, component.Url.AbsoluteUri, archive, component.Size, pause,
                CancellationToken.None, (_, detail) => { if (detail is not null) StartupDiagnostics.Write(detail); });
            var hash = await InstallerForm.ComputeSha256Async(archive, pause, CancellationToken.None);
            if (!hash.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{component.Id} SHA-256 mismatch");
            var destination = InstallerForm.ComponentDestination(installRoot, component);
            Directory.CreateDirectory(destination);
            await InstallerForm.ExtractArchiveAsync(archive, destination, pause, CancellationToken.None,
                new Progress<ItemProgress>());
            await InstallerForm.VerifyFilesAsync(destination, component.Files, pause, CancellationToken.None,
                new Progress<ItemProgress>());
            File.Delete(archive);
        }
        var uninstaller = Path.Combine(installRoot, "uninstaller.exe");
        File.Copy(Environment.ProcessPath!, uninstaller, true);
        var packagedUpdater = Path.Combine(installRoot, "current", "updater", "MLCCS.VideoSearch.Updater.exe");
        File.Copy(packagedUpdater, Path.Combine(installerRoot, "MLCCS.VideoSearch.Updater.exe"), true);
        await File.WriteAllTextAsync(Path.Combine(installerRoot, "installed.json"),
            JsonSerializer.Serialize(new { version = manifest.ProductVersion, installedUtc = DateTimeOffset.UtcNow,
                components = selected.Select(item => new { item.Id, item.Sha256 }) },
                new JsonSerializerOptions { WriteIndented = true }));
        FeedbackState.MarkV1Installed();
        var entryPoint = Path.Combine(installRoot, manifest.EntryPoint);
        Shortcut.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs",
            "MLCCS", $"{Program.ProductName}.lnk"), entryPoint, installRoot, "本地视频语义搜索");
        using var key = Registry.CurrentUser.CreateSubKey(Program.UninstallKey);
        key.SetValue("DisplayName", Program.ProductName);
        key.SetValue("DisplayVersion", Program.Version);
        key.SetValue("Publisher", "MLCCS");
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("DisplayIcon", entryPoint);
        key.SetValue("UninstallString", $"\"{uninstaller}\"");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
    }
}

internal sealed class PauseController
{
    private readonly object _sync = new();
    private TaskCompletionSource _resumeSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsPaused { get; private set; }

    internal void Pause()
    {
        lock (_sync)
        {
            if (IsPaused)
            {
                return;
            }
            IsPaused = true;
            _resumeSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void Resume()
    {
        lock (_sync)
        {
            if (!IsPaused)
            {
                return;
            }
            IsPaused = false;
            _resumeSignal.TrySetResult();
        }
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return IsPaused
                ? _resumeSignal.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask;
        }
    }
}

internal readonly record struct ItemProgress(int Completed, int Total);

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();
    private static string? _logPath;

    internal static string LogPath =>
        _logPath ?? Path.Combine(Path.GetTempPath(), "MLCCS-VideoSearch-Installer", "startup.log");

    internal static void Initialize(string[] args)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MLCCS", "VideoSearch", "Installer");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, "startup.log");
        }
        catch
        {
            var directory = Path.Combine(Path.GetTempPath(), "MLCCS-VideoSearch-Installer");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(directory, "startup.log");
        }

        Write("------------------------------------------------------------");
        Write($"Starting {Program.ProductName} installer {Program.Version}.");
        Write($"Executable: {Environment.ProcessPath ?? "(unknown)"}");
        Write($"OS: {RuntimeInformation.OSDescription}; framework: {RuntimeInformation.FrameworkDescription}");
        Write($"Process architecture: {RuntimeInformation.ProcessArchitecture}; OS architecture: {RuntimeInformation.OSArchitecture}");
        Write($"Command line mode: {(args.Length == 0 ? "install" : string.Join(' ', args.Select(RedactArgument)))}");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            Write($"Unhandled AppDomain exception (terminating={eventArgs.IsTerminating}): {eventArgs.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Write($"Unobserved task exception: {eventArgs.Exception}");
            eventArgs.SetObserved();
        };
    }

    internal static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var safe = DiagnosticRedactor.Redact(message, Environment.UserName);
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} [PID {Environment.ProcessId}] {safe}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never prevent the installer from opening.
        }
    }

    internal static void ReportFatal(string stage, Exception exception)
    {
        Write($"FATAL during {stage}: {exception}");
        var message =
            $"安装器无法继续运行（{stage}）。\n\n{exception.Message}\n\n诊断日志：\n{LogPath}";
        try
        {
            NativeMethods.MessageBoxW(IntPtr.Zero, message,
                $"{Program.ProductName} 安装器启动失败",
                NativeMethods.MbOk | NativeMethods.MbIconError | NativeMethods.MbSetForeground);
        }
        catch
        {
            // If user32 itself is unavailable, the startup log remains available.
        }
    }

    private static string RedactArgument(string argument)
    {
        return argument.Contains("key", StringComparison.OrdinalIgnoreCase) ||
               argument.Contains("token", StringComparison.OrdinalIgnoreCase)
            ? "[redacted]"
            : argument;
    }

    private static class NativeMethods
    {
        internal const uint MbOk = 0x00000000;
        internal const uint MbIconError = 0x00000010;
        internal const uint MbSetForeground = 0x00010000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int MessageBoxW(
            IntPtr windowHandle,
            string text,
            string caption,
            uint type);
    }
}

internal static class Shortcut
{
    internal static void Create(string path, string target, string workingDirectory, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellLinkType = Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
        var link = (IShellLinkW)Activator.CreateInstance(shellLinkType)!;
        link.SetPath(target);
        link.SetWorkingDirectory(workingDirectory);
        link.SetDescription(description);
        ((IPersistFile)link).Save(path, true);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotKey(out short hotkey);
        void SetHotKey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr iconPath, int cch, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
