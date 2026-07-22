using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JeekTools;

/// <summary>JSON load/save helpers for settings files, including a three-way
/// merge that lets multiple app instances write concurrently without losing
/// each other's changes.</summary>
public static class JsonSettingsFile
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool TryLoad<T>(string path, out T settings)
        where T : new()
    {
        settings = new T();

        try
        {
            if (!File.Exists(path))
                return false;

            settings = JsonSerializer.Deserialize<T>(
                File.ReadAllText(path),
                JsonOptions) ?? new T();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Serialize<T>(T settings) =>
        JsonSerializer.Serialize(settings, JsonOptions);

    public static T Clone<T>(T value) where T : class, new() =>
        JsonSerializer.Deserialize<T>(Serialize(value), JsonOptions) ?? new T();

    /// <summary>
    /// Writes <paramref name="local"/> to <paramref name="path"/> under a
    /// cross-process lock, keeping changes another process saved since
    /// <paramref name="baseline"/> was read: only properties that differ between
    /// baseline and local overwrite the on-disk values. With
    /// <paramref name="forceAllLocal"/> the disk state is ignored and all local
    /// values win (used when writing to a brand-new file location).
    /// </summary>
    public static bool TryMergeAndWrite<T>(
        string path,
        T baseline,
        T local,
        Action<T> normalize,
        bool forceAllLocal,
        out T merged)
        where T : class, new()
    {
        merged = local;
        try
        {
            using var lease = SharedDataFile.Acquire(path);
            var latest = TryLoad(path, out T disk) ? disk : Clone(baseline);
            normalize(latest);

            if (forceAllLocal)
            {
                merged = Clone(local);
            }
            else
            {
                var baselineNode = JsonSerializer.SerializeToNode(baseline, JsonOptions) as JsonObject ?? new();
                var localNode = JsonSerializer.SerializeToNode(local, JsonOptions) as JsonObject ?? new();
                var resultNode = JsonSerializer.SerializeToNode(latest, JsonOptions) as JsonObject ?? new();
                foreach (var property in localNode)
                {
                    baselineNode.TryGetPropertyValue(property.Key, out var baselineValue);
                    if (!JsonNode.DeepEquals(property.Value, baselineValue))
                        resultNode[property.Key] = property.Value?.DeepClone();
                }
                merged = resultNode.Deserialize<T>(JsonOptions) ?? new T();
            }

            normalize(merged);
            SharedDataFile.WriteAllTextAtomic(path, Serialize(merged));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
