namespace ZeroMcp.Relay.Server;

public sealed class RunOptions
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5000;

    public bool Stdio { get; set; }

    public bool EnableUi { get; set; }

    public string? ConfigPath { get; set; }

    public bool ValidateOnStart { get; set; } = true;

    public bool Lazy { get; set; }
}

public static class RunOptionsParser
{
    public static RunOptions Parse(string[] args)
    {
        var options = new RunOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--host":
                    options.Host = ReadValue(args, ref i, "--host");
                    break;
                case "--port":
                    options.Port = int.Parse(ReadValue(args, ref i, "--port"));
                    break;
                case "--stdio":
                    options.Stdio = true;
                    break;
                case "--enable-ui":
                    options.EnableUi = true;
                    break;
                case "--config":
                    options.ConfigPath = ReadValue(args, ref i, "--config");
                    break;
                case "--lazy":
                    options.Lazy = true;
                    break;
                case "--validate-on-start":
                    options.ValidateOnStart = true;
                    break;
            }
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }
}
