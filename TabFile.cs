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

        var lines = File.ReadAllLines(tabPath);

        Rows.Clear();
        foreach (var line in lines)
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
}
