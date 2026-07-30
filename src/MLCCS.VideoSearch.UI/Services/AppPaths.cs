namespace MLCCS.VideoSearch.UI.Services;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLCCS", "VideoSearch");
    public static string Database => Path.Combine(Root, "catalog.db");
    public static string Configuration => Path.Combine(Root, "libraries.json");
    public static string IndexStatus => Path.Combine(Root, "real-index-status.json");
}
