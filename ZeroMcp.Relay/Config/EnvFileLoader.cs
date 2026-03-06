namespace ZeroMcp.Relay.Config;

public static class EnvFileLoader
{
    public static void Load(string envPath)
    {
        var absolute = Path.GetFullPath(envPath);
        if (!File.Exists(absolute))
        {
            throw new InvalidOperationException($"Env file '{absolute}' does not exist.");
        }

        foreach (var rawLine in File.ReadAllLines(absolute))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (split.Length == 2 && !string.IsNullOrWhiteSpace(split[0]))
            {
                Environment.SetEnvironmentVariable(split[0], split[1]);
            }
        }
    }
}
