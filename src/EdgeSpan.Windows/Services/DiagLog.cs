namespace EdgeSpan.Windows.Services;

/// <summary>Append-only log the user can send when USB or input fails.</summary>
public static class DiagLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EdgeSpan",
        "edgestpan.log");

    public static void Write(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine;
            lock (FilePath)
            {
                File.AppendAllText(FilePath, text);
                var info = new FileInfo(FilePath);
                if (info.Length <= 200_000)
                    return;
                var keep = File.ReadAllText(FilePath);
                var cut = keep.Length / 2;
                var nl = keep.IndexOf('\n', cut);
                File.WriteAllText(FilePath, keep[(nl < 0 ? cut : nl + 1)..]);
            }
        }
        catch (IOException)
        {
        }
    }

    public static string Tail(int lines = 8)
    {
        try
        {
            if (!File.Exists(FilePath))
                return "";
            var all = File.ReadAllLines(FilePath);
            return string.Join(Environment.NewLine, all.TakeLast(lines));
        }
        catch (IOException)
        {
            return "";
        }
    }
}
