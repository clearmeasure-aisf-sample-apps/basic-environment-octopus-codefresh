namespace Platform.Conformance.Report;

/// <summary>A parsed command line: the command name and its <c>--option value</c> pairs (options may repeat).</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> options;

    private CommandLine(string command, Dictionary<string, List<string>> options)
    {
        Command = command;
        this.options = options;
    }

    /// <summary>The command, for example <c>report</c>.</summary>
    public string Command { get; }

    /// <summary>Parses <c>command --name value [--name value ...]</c>.</summary>
    /// <param name="args">Process arguments.</param>
    /// <param name="allowed">Option names the command accepts, without dashes.</param>
    /// <exception cref="CommandLineException">An option is unknown or lacks a value.</exception>
    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, IReadOnlySet<string>> allowed)
    {
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            throw new CommandLineException("Name a command: map, report or render-catalogue.");
        }

        var command = args[0];
        if (!allowed.TryGetValue(command, out var names))
        {
            throw new CommandLineException($"Unknown command '{command}'; use map, report or render-catalogue.");
        }

        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length == 2)
            {
                throw new CommandLineException($"Unexpected argument '{argument}'; options look like --name value.");
            }

            var name = argument[2..];
            if (!names.Contains(name))
            {
                throw new CommandLineException($"'{command}' has no option --{name}; options: {string.Join(", ", names.Order(StringComparer.Ordinal).Select(option => $"--{option}"))}.");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Option --{name} needs a value.");
            }

            index++;
            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options[name] = values;
            }

            values.Add(args[index]);
        }

        return new CommandLine(command, options);
    }

    /// <summary>Every value of a repeatable option.</summary>
    /// <param name="name">Option name without dashes.</param>
    public IReadOnlyList<string> All(string name) => options.TryGetValue(name, out var values) ? values : [];

    /// <summary>The value of a single-valued option, or <c>null</c>.</summary>
    /// <param name="name">Option name without dashes.</param>
    /// <exception cref="CommandLineException">The option was given more than once.</exception>
    public string? Single(string name) => All(name) switch
    {
        [] => null,
        [var only] => only,
        _ => throw new CommandLineException($"Option --{name} may be given once."),
    };
}

/// <summary>The command line is invalid; the message says how to fix it.</summary>
internal sealed class CommandLineException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public CommandLineException(string message)
        : base(message)
    {
    }
}
