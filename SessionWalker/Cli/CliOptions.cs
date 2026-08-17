using System;

namespace SessionWalker.Cli;

public enum OutputFormat
{
    Console,
    Json,
    Csv
}

/// <summary>
/// Parsed representation of a CLI invocation. Deliberately hand-rolled
/// rather than pulling in a full argument-parsing library, since the
/// surface area (one subcommand with a handful of flags, plus a
/// zero-argument subcommand) is small and stable.
/// </summary>
public sealed class CliOptions
{
    public string Command { get; private set; } = string.Empty;

    public string SolutionPath { get; private set; }

    public bool SessionOnly { get; private set; }

    public bool SessionWriteOnly { get; private set; }

    public bool ControllerOnly { get; private set; }

    public bool Verbose { get; private set; }

    public string ProjectFilter { get; private set; }

    public string JsonOutputPath { get; private set; }

    public string CsvOutputPath { get; private set; }

    public static CliOptions Parse(string[] args, out string error)
    {
        error = null;

        if (args.Length == 0)
        {
            error = "No command specified. Try 'CSharpWalker analyze <solution.sln>' or 'CSharpWalker list-analyzers'.";
            return null;
        }

        var command = args[0];

        if (string.Equals(command, "list-analyzers", StringComparison.OrdinalIgnoreCase))
        {
            return new CliOptions { Command = "list-analyzers" };
        }

        if (!string.Equals(command, "analyze", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown command '{command}'. Supported commands: analyze, list-analyzers.";
            return null;
        }

        if (args.Length < 2)
        {
            error = "The 'analyze' command requires a path to a .sln file.";
            return null;
        }

        var solutionPath = args[1];
        bool sessionOnly = false, sessionWriteOnly = false, controllerOnly = false, verbose = false;
        string projectFilter = null, jsonPath = null, csvPath = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session":
                    sessionOnly = true;
                    break;
                case "--session-write":
                    sessionWriteOnly = true;
                    break;
                case "--controller-only":
                    controllerOnly = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--project":
                    if (i + 1 >= args.Length)
                    {
                        error = "--project requires a value.";
                        return null;
                    }
                    projectFilter = args[++i];
                    break;
                case "--json":
                    if (i + 1 >= args.Length)
                    {
                        error = "--json requires an output file path.";
                        return null;
                    }
                    jsonPath = args[++i];
                    break;
                case "--csv":
                    if (i + 1 >= args.Length)
                    {
                        error = "--csv requires an output file path.";
                        return null;
                    }
                    csvPath = args[++i];
                    break;
                default:
                    error = $"Unrecognized argument '{args[i]}'.";
                    return null;
            }
        }

        return new CliOptions
        {
            Command = "analyze",
            SolutionPath = solutionPath,
            SessionOnly = sessionOnly,
            SessionWriteOnly = sessionWriteOnly,
            ControllerOnly = controllerOnly,
            Verbose = verbose,
            ProjectFilter = projectFilter,
            JsonOutputPath = jsonPath,
            CsvOutputPath = csvPath
        };
    }
}
