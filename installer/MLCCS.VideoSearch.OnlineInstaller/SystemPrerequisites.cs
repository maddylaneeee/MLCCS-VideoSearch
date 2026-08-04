using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace MLCCS.VideoSearch.OnlineInstaller;

internal sealed record PrerequisiteReport(
    bool SupportedWindows,
    bool SupportedArchitecture,
    bool VisualCppRuntimeReady,
    bool MediaFoundationReady,
    bool WindowsCoreReady,
    bool IsWindowsMediaEdition,
    int WindowsBuild,
    string WindowsName,
    IReadOnlyList<string> MissingFiles)
{
    internal bool HasRepairableIssues =>
        !VisualCppRuntimeReady || !MediaFoundationReady || !WindowsCoreReady;

    internal bool CanInstall => SupportedWindows && SupportedArchitecture;

    internal string Describe()
    {
        var items = new List<string>
        {
            $"{WindowsName}（build {WindowsBuild}）：{(SupportedWindows ? "支持" : "需要 Windows 10 1809+/Windows 11")}",
            $"64 位 Windows：{(SupportedArchitecture ? "正常" : "不支持当前架构")}",
            $"Microsoft Visual C++ x64 运行库：{(VisualCppRuntimeReady ? "已就绪" : "需要下载/更新")}",
            $"Windows 媒体组件：{(MediaFoundationReady ? "已就绪" : "需要从 Windows Update 补齐")}",
            $"Windows 核心组件：{(WindowsCoreReady ? "已就绪" : "需要 DISM/SFC 修复")}",
            "Windows App SDK、.NET 10、Python/PyTorch CUDA 与 Qdrant：随程序提供"
        };
        if (MissingFiles.Count > 0)
        {
            items.Add($"缺失文件：{string.Join("、", MissingFiles)}");
        }
        return string.Join(Environment.NewLine, items);
    }
}

internal sealed class PrerequisiteRebootRequiredException(string message) : Exception(message);

internal static class SystemPrerequisites
{
    internal const string VisualCppDownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";
    private static readonly Version MinimumVisualCppVersion = new(14, 40, 33810, 0);

    private static readonly string[] CoreWindowsFiles =
    [
        "bcrypt.dll", "combase.dll", "crypt32.dll", "d3d12.dll", "dcomp.dll",
        "dwrite.dll", "dxgi.dll", "kernel32.dll", "ole32.dll", "shell32.dll",
        "user32.dll", "windowscodecs.dll", "winhttp.dll"
    ];

    private static readonly string[] MediaFoundationFiles =
    [
        "evr.dll", "mf.dll", "mfplat.dll", "mfreadwrite.dll"
    ];

    private static readonly string[] VisualCppFiles =
    [
        "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"
    ];

    internal static PrerequisiteReport Inspect()
    {
        var os = Environment.OSVersion.Version;
        var windowsName = ReadWindowsName();
        var edition = ReadWindowsEdition();
        var installationType = ReadWindowsInstallationType();
        var isMediaEdition = edition.EndsWith("N", StringComparison.OrdinalIgnoreCase) ||
                             edition.EndsWith("KN", StringComparison.OrdinalIgnoreCase) ||
                             windowsName.Contains(" N", StringComparison.OrdinalIgnoreCase) ||
                             windowsName.Contains(" KN", StringComparison.OrdinalIgnoreCase);
        var systemDirectory = Environment.SystemDirectory;
        var missingCore = Missing(systemDirectory, CoreWindowsFiles);
        var missingMedia = Missing(systemDirectory, MediaFoundationFiles);
        var missingVisualCpp = Missing(systemDirectory, VisualCppFiles);
        var visualCppVersion = ReadVisualCppVersion(systemDirectory);

        return new PrerequisiteReport(
            OperatingSystem.IsWindows() &&
            IsSupportedWindowsClient(os, windowsName, installationType),
            RuntimeInformation.OSArchitecture == Architecture.X64 &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            missingVisualCpp.Count == 0 && visualCppVersion >= MinimumVisualCppVersion,
            missingMedia.Count == 0,
            missingCore.Count == 0,
            isMediaEdition,
            os.Build,
            windowsName,
            missingCore.Concat(missingMedia).Concat(missingVisualCpp)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static async Task<bool> EnsureAsync(
        HttpClient http,
        string cacheDirectory,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var report = Inspect();
        if (!report.CanInstall)
        {
            throw new PlatformNotSupportedException(report.Describe());
        }

        Directory.CreateDirectory(cacheDirectory);
        var rebootRequired = false;
        if (!report.VisualCppRuntimeReady)
        {
            progress("正在从 Microsoft 下载 Visual C++ x64 运行库…");
            var redistPath = Path.Combine(cacheDirectory, "vc_redist.x64.exe");
            await DownloadMicrosoftExecutableAsync(http, VisualCppDownloadUrl, redistPath,
                80L * 1024 * 1024, progress, cancellationToken);
            AuthenticodeVerifier.RequireTrustedPublisher(redistPath, "Microsoft Corporation");
            progress("正在安装 Microsoft Visual C++ x64 运行库（可能显示 UAC 提示）…");
            rebootRequired |= RunElevated(redistPath, "/install /quiet /norestart");
        }

        report = Inspect();
        if (!report.MediaFoundationReady && report.IsWindowsMediaEdition)
        {
            progress("正在通过 Windows Update 安装 Media Feature Pack（可能显示 UAC 提示）…");
            rebootRequired |= RunElevated(
                Path.Combine(Environment.SystemDirectory, "dism.exe"),
                "/Online /Add-Capability /CapabilityName:Media.MediaFeaturePack~~~~0.0.1.0 /NoRestart");
        }

        report = Inspect();
        if (!report.WindowsCoreReady || !report.MediaFoundationReady)
        {
            progress("正在从 Windows Update 修复缺失的系统组件（DISM）…");
            rebootRequired |= RunElevated(
                Path.Combine(Environment.SystemDirectory, "dism.exe"),
                "/Online /Cleanup-Image /RestoreHealth /NoRestart");
            progress("正在校验并修复 Windows 系统文件（SFC）…");
            rebootRequired |= RunElevated(
                Path.Combine(Environment.SystemDirectory, "sfc.exe"),
                "/scannow");
        }

        report = Inspect();
        if (rebootRequired)
        {
            throw new PrerequisiteRebootRequiredException(
                "基础依赖已安装或修复，但 Windows 要求重启。请重启电脑后重新运行安装器；已下载文件会保留。");
        }
        if (report.HasRepairableIssues)
        {
            throw new InvalidOperationException(
                "基础依赖复检仍未通过。请先完成 Windows Update（包括可选更新）并重启，再重试。\n\n" +
                report.Describe());
        }
        return true;
    }

    internal static bool IsAllowedMicrosoftDownloadHost(string host)
    {
        return host.Equals("aka.ms", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("download.visualstudio.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSupportedWindowsClient(
        Version version,
        string productName,
        string installationType)
    {
        return version.Major >= 10 && version.Build >= 17763 &&
               installationType.Equals("Client", StringComparison.OrdinalIgnoreCase) &&
               !productName.Contains("Server", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task DownloadMicrosoftExecutableAsync(
        HttpClient http,
        string url,
        string outputPath,
        long maximumBytes,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var partialPath = outputPath + ".partial";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Microsoft 依赖下载未返回最终地址。");
        if (finalUri.Scheme != Uri.UriSchemeHttps || !IsAllowedMicrosoftDownloadHost(finalUri.Host))
        {
            throw new InvalidDataException($"依赖下载重定向到了不受信任的主机：{finalUri.Host}");
        }
        if (response.Content.Headers.ContentLength is > 0 and var contentLength &&
            contentLength > maximumBytes)
        {
            throw new InvalidDataException("依赖安装包大小超过安全上限。");
        }

        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[256 * 1024];
            long downloaded = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                downloaded += read;
                if (downloaded > maximumBytes)
                {
                    throw new InvalidDataException("依赖安装包大小超过安全上限。");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress($"正在下载 Visual C++ x64 运行库：{downloaded / 1024d / 1024d:0.0} MB");
            }
            await output.FlushAsync(cancellationToken);
        }
        File.Move(partialPath, outputPath, overwrite: true);
    }

    private static bool RunElevated(string executable, string arguments)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("找不到 Windows 依赖安装/修复工具。", executable);
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            }) ?? throw new InvalidOperationException("未能启动依赖安装程序。");
            process.WaitForExit();
            return process.ExitCode switch
            {
                0 => false,
                1641 or 3010 => true,
                _ => throw new InvalidOperationException(
                    $"依赖安装/修复程序返回错误代码 {process.ExitCode}。请检查 Windows Update 后重试。")
            };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("用户取消了 Windows 管理员权限请求。", exception);
        }
    }

    private static IReadOnlyList<string> Missing(string directory, IEnumerable<string> names) =>
        names.Where(name => !File.Exists(Path.Combine(directory, name))).ToArray();

    private static string ReadWindowsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("ProductName") as string ?? RuntimeInformation.OSDescription;
    }

    private static string ReadWindowsEdition()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("EditionID") as string ?? string.Empty;
    }

    private static string ReadWindowsInstallationType()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("InstallationType") as string ?? string.Empty;
    }

    private static Version ReadVisualCppVersion(string systemDirectory)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        var registryVersion = (key?.GetValue("Installed") as int? ?? 0) == 1
            ? Version.TryParse((key?.GetValue("Version") as string)?.TrimStart('v'), out var parsed)
                ? parsed : new Version()
            : new Version();
        var runtimePath = Path.Combine(systemDirectory, "vcruntime140.dll");
        var fileVersion = File.Exists(runtimePath)
            ? FileVersionInfo.GetVersionInfo(runtimePath).FileVersion
            : null;
        var parsedFileVersion = Version.TryParse(fileVersion, out var fileParsed)
            ? fileParsed : new Version();
        return registryVersion > parsedFileVersion ? registryVersion : parsedFileVersion;
    }
}

internal static class AuthenticodeVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static void RequireTrustedPublisher(string path, string expectedPublisher)
    {
        var trustData = CreateTrustData(path);
        try
        {
            if (WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, trustData) != 0)
            {
                throw new CryptographicException("Microsoft 依赖安装包的 Authenticode 签名无效。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(trustData.FileInfo);
        }
#pragma warning disable SYSLIB0057 // The signer certificate is read from an Authenticode file after WinVerifyTrust.
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        if (!certificate.Subject.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                $"依赖安装包签名发布者不是 {expectedPublisher}：{certificate.Subject}");
        }
    }

    private static WinTrustData CreateTrustData(string path) => new WinTrustData
    {
        StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
        PolicyCallbackData = IntPtr.Zero,
        SipClientData = IntPtr.Zero,
        UiChoice = 2,
        RevocationChecks = 0,
        UnionChoice = 1,
        FileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>()),
        StateAction = 0,
        StateData = IntPtr.Zero,
        UrlReference = IntPtr.Zero,
        ProviderFlags = 0x00000010,
        UiContext = 0,
        SignatureSettings = IntPtr.Zero
    }.WithFile(path);

    private static WinTrustData WithFile(this WinTrustData data, string path)
    {
        var info = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };
        Marshal.StructureToPtr(info, data.FileInfo, false);
        return data;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        internal uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] internal string FilePath = string.Empty;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData
    {
        internal uint StructSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }
}
