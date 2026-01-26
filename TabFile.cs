using System.Text;

namespace JeekTools;

public class TabFile
{
    public const char Separator = '\t';

    public List<List<string>> Rows { get; } = new();

    public bool Load(string tabPath)
    {
        if (!File.Exists(tabPath))
            return false;

        Rows.Clear();
        foreach (var line in File.ReadAllLines(tabPath))
            if (!string.IsNullOrEmpty(line))
                Rows.Add(line.Split(Separator).ToList());

        return true;
    }

    public async Task<bool> LoadAsync(string tabPath)
    {
        if (!File.Exists(tabPath))
            return false;

        Rows.Clear();
        await foreach (var line in File.ReadLinesAsync(tabPath))
            if (!string.IsNullOrEmpty(line))
                Rows.Add(line.Split(Separator).ToList());

        return true;
    }

    public void Save(string tabPath)
    {
        using var writer = new StreamWriter(tabPath, false, new UTF8Encoding(true));
        foreach (var row in Rows)
            writer.WriteLine(string.Join(Separator, row));
    }

    public async Task SaveAsync(string tabPath)
    {
        await using var writer = new StreamWriter(tabPath, false, new UTF8Encoding(true));
        foreach (var row in Rows)
            await writer.WriteLineAsync(string.Join(Separator, row));
    }
}
