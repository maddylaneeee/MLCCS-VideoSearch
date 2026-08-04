using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using MLCCS.VideoSearch.UI.Models;

namespace MLCCS.VideoSearch.UI.Services;

internal static class SearchSessionState
{
    public static ObservableCollection<SearchResultViewModel> Results { get; } = [];
    public static string Query { get; set; } = "";
    public static string Source { get; set; } = "all";
    public static string Library { get; set; } = "";
    public static string Extension { get; set; } = "";
    public static string Duration { get; set; } = "any";
    public static string ModifiedDays { get; set; } = "any";
    public static string IndexStatus { get; set; } = "";
    public static string Sort { get; set; } = "relevance";
    public static string Title { get; set; } = "";
    public static string Message { get; set; } = "";
    public static InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;
    public static bool HasCompletedSearch { get; set; }
    public static bool GridMode { get; set; }
}
