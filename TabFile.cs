using System.Text;

namespace JeekTools;

public class TabFile
{
    public const char Separator = '\t';
    private const string DefaultNewLine = "\n";

    public List<List<string>> Rows { get; } = new();
    public string NewLine { get; private set; } = DefaultNewLine;
    public bool HasUtf8Bom { get; private set; }

    public bool Load(string tabPath)
    {
        if (!File.Exists(tabPath))
            return false;

        DetectFileFormat(tabPath);
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

        DetectFileFormat(tabPath);
        Rows.Clear();
        await foreach (var line in File.ReadLinesAsync(tabPath))
            if (!string.IsNullOrEmpty(line))
                Rows.Add(line.Split(Separator).ToList());

        return true;
    }

    public void Save(string tabPath)
    {
        using var writer = new StreamWriter(tabPath, false, new UTF8Encoding(HasUtf8Bom));
        writer.NewLine = NewLine;
        foreach (var row in Rows)
            writer.WriteLine(string.Join(Separator, row));
    }

    public async Task SaveAsync(string tabPath)
    {
        await using var writer = new StreamWriter(tabPath, false, new UTF8Encoding(HasUtf8Bom));
        writer.NewLine = NewLine;
        foreach (var row in Rows)
            await writer.WriteLineAsync(string.Join(Separator, row));
    }

    private void DetectFileFormat(string tabPath)
    {
        var bytes = File.ReadAllBytes(tabPath);
        HasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
        NewLine = DetectNewLine(bytes);
    }

    private static string DetectNewLine(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '\r')
                return i + 1 < bytes.Length && bytes[i + 1] == '\n' ? "\r\n" : "\r";

            if (bytes[i] == '\n')
                return "\n";
        }

        return DefaultNewLine;
    }
}
