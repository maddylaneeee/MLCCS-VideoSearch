using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MLCCS.VideoSearch.UI.Services;

internal static class FolderPickerService
{
    public static async Task<string?> PickAsync()
    {
        var window = App.CurrentWindow ?? throw new InvalidOperationException("主窗口尚未创建。");
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            CommitButtonText = "添加到资源库"
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
