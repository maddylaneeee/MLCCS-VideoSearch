using System.Text.Json;
using System.Text.Json.Nodes;

namespace MLCCS.VideoSearch.Core.Contracts;

public static class SettingsMigrator
{
    public static AppSettings Migrate(string json, string defaultRoot)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Settings must be an object.");
        var version = node["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version > AppSettings.CurrentSchemaVersion) throw new ProtocolException("SETTINGS_VERSION_NEWER", "Settings were written by a newer application.");
        if (version == 0)
        {
            var defaults = AppSettings.CreateDefault(defaultRoot);
            node["schemaVersion"] = 1;
            node["privacy"] ??= JsonSerializer.SerializeToNode(defaults.Privacy, JsonDefaults.Options);
            node["indexing"] ??= JsonSerializer.SerializeToNode(defaults.Indexing, JsonDefaults.Options);
            node["search"] ??= JsonSerializer.SerializeToNode(defaults.Search, JsonDefaults.Options);
            node["updates"] ??= JsonSerializer.SerializeToNode(defaults.Updates, JsonDefaults.Options);
            node["storage"] ??= JsonSerializer.SerializeToNode(defaults.Storage, JsonDefaults.Options);
        }
        return node.Deserialize<AppSettings>(JsonDefaults.Options) ?? throw new JsonException("Settings migration failed.");
    }
}

