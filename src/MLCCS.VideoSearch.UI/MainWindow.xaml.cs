using Microsoft.UI.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MLCCS.VideoSearch.UI.Pages;
using MLCCS.VideoSearch.UI.Services;
using WinRT.Interop;

namespace MLCCS.VideoSearch.UI;

public sealed partial class MainWindow : Window
{
    private bool _updateCheckStarted;
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        if (!File.Exists(AppPaths.Configuration))
        {
            Navigation.SelectedItem = null;
            ContentFrame.Navigate(typeof(FirstRunPage));
        }
        else
        {
            Navigation.SelectedItem = Navigation.MenuItems[1];
            ContentFrame.Navigate(typeof(LibraryPage));
        }
        Activated += async (_, _) =>
        {
            if (_updateCheckStarted) return;
            _updateCheckStarted = true;
            await CheckForUpdatesAsync();
        };
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await AgentClient.RequestAsync("update.check", new { interactive = false });
            if (!result.TryGetProperty("available", out var available) || !available.GetBoolean()) return;
            var version = result.GetProperty("version").GetString()!;
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = $"MLCCS Video Search {version} 可用",
                Content = result.GetProperty("releaseNotes").GetString(),
                PrimaryButtonText = "立即更新",
                SecondaryButtonText = "稍后",
                CloseButtonText = "跳过此版本",
                DefaultButton = ContentDialogButton.Primary
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary)
                await AgentClient.RequestAsync("update.apply");
            else if (choice == ContentDialogResult.None)
                await AgentClient.RequestAsync("update.skip", new { version });
        }
        catch
        {
            // Automatic checks are intentionally non-blocking. Manual checks surface errors in Settings.
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        if (tag is null) return;
        ContentFrame.Navigate(tag switch
        {
            "search" => typeof(SearchPage),
            "jobs" => typeof(JobsPage),
            _ => typeof(LibraryPage)
        });
    }

    public void NavigateToLibrary()
    {
        Navigation.SelectedItem = Navigation.MenuItems[1];
        ContentFrame.Navigate(typeof(LibraryPage));
    }
}
