namespace JeekTools;

public static class EnvironmentHelper
{
    public static string FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path == null)
            return "";

        foreach (var dir in path.Split(';'))
        {
            if (dir == "")
                continue;

            var filePath = Path.Combine(dir, fileName);
            if (File.Exists(filePath))
                return filePath;
        }

        return "";
    }

    public static List<string> FindAllInPath(string fileName)
    {
        var result = new List<string>();

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path == null)
            return result;

        foreach (var dir in path.Split(';'))
        {
            if (dir == "")
                continue;

            var filePath = Path.Combine(dir, fileName);
            if (File.Exists(filePath))
                result.Add(filePath);
        }

        return result;
    }
}
