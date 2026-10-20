namespace AirPodsHandoff;

static class Log
{
    static readonly object Gate = new();

    public static string FilePath => Path.Combine(Config.Dir, "log.txt");

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
