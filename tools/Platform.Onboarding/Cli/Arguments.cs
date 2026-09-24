namespace Platform.Onboarding.Cli;

/// <summary>The command line is wrong; the tool prints the message and the usage, and exits with 2.</summary>
/// <param name="message">What is wrong.</param>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Parsed arguments of one command: positionals, options with values and flags.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> values;
    private readonly HashSet<string> flags;

    private Arguments(IReadOnlyList<string> positionals, Dictionary<string, string> values, HashSet<string> flags)
    {
        Positionals = positionals;
        this.values = values;
        this.flags = flags;
    }

    /// <summary>Arguments that are not options.</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>
    /// Parses <paramref name="args"/>. Options take the form <c>--name value</c> or <c>--name=value</c>; flags take no value.
    /// </summary>
    /// <param name="args">Arguments after the command name.</param>
    /// <param name="valueOptions">Options that take a value (without the dashes).</param>
    /// <param name="flagOptions">Options without a value (without the dashes).</param>
    /// <exception cref="UsageException">An unknown option, a repeated option or a missing value.</exception>
    public static Arguments Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var positionals = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--")
            {
                positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? inline = null;
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (flagOptions.Contains(name))
            {
                if (inline is not null)
                {
                    throw new UsageException($"--{name} takes no value");
                }

                flags.Add(name);
                continue;
            }

            if (!valueOptions.Contains(name))
            {
                throw new UsageException($"unknown option --{name}");
            }

            if (values.ContainsKey(name))
            {
                throw new UsageException($"--{name} is given twice");
            }

            if (inline is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"--{name} needs a value");
                }

                inline = args[++i];
            }

            values[name] = inline;
        }

        return new Arguments(positionals, values, flags);
    }

    /// <summary>The value of an option, or <c>null</c>.</summary>
    /// <param name="name">Option name without dashes.</param>
    public string? Value(string name) => values.GetValueOrDefault(name);

    /// <summary><c>true</c> when the flag was given.</summary>
    /// <param name="name">Flag name without dashes.</param>
    public bool Flag(string name) => flags.Contains(name);

    /// <summary>The single positional argument.</summary>
    /// <param name="what">What it is, for the message.</param>
    /// <exception cref="UsageException">None or several were given.</exception>
    public string Single(string what) => Positionals.Count == 1 ? Positionals[0] : throw new UsageException($"give exactly one {what}");
}
