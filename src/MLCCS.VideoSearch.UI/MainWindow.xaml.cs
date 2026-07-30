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
