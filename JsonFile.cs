using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace JeekTools;

public class JsonFile<T>
    where T : class
{
    public string FilePath { get; set; }

    public JsonFile(string filePath)
    {
        FilePath = filePath;
    }

    public static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public async Task<T?> Load()
    {
        if (!File.Exists(FilePath))
            return null;

        await using var fileStream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<T>(fileStream, JsonSerializerOptions);
    }

    public async Task Save(T obj)
    {
        await using var fileStream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(fileStream, obj, JsonSerializerOptions);
    }

    public static T? FromJson(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions);
    }

    public static string ToJson(T obj)
    {
        return JsonSerializer.Serialize(obj, JsonSerializerOptions);
    }
}
