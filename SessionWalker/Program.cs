using SessionWalker.Cli;
using SessionWalker.Core.Interfaces;
using SessionWalker.Infrastructure.Output;

namespace SessionWalker
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            var arguments = string.Join(" ", args);
            args = args.Append("--verbose").ToArray();
            var options = CliOptions.Parse(args, out var error);

            if (options is null)
            {
                await Console.Error.WriteLineAsync($"Error: {error}");
                await Console.Error.WriteLineAsync();
                PrintUsage();
                return 1;
            }

            if (options.Command == "list-analyzers")
            {
                Console.WriteLine("Registered analyzers:");
                foreach (var analyzer in AnalysisOrchestrator.CreateAnalyzers())
                {
                    Console.WriteLine($"  - {analyzer.Name}: {analyzer.Description}");
                }
                return 0;
            }

            if (!File.Exists(options.SolutionPath))
            {
                await Console.Error.WriteLineAsync($"Error: solution file not found: {options.SolutionPath}");
                return 1;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                var orchestrator = new AnalysisOrchestrator();
                var result = await orchestrator.RunAsync(options, cts.Token);

                var writers = new List<IOutputWriter>();

                if (!string.IsNullOrEmpty(options.JsonOutputPath))
                {
                    writers.Add(new JsonOutputWriter());
                }

                if (!string.IsNullOrEmpty(options.CsvOutputPath))
                {
                    writers.Add(new CsvOutputWriter());
                }

                // Console output is always shown unless the person redirected *both*
                // requested outputs to files and gave nothing else to look at — in that
                // case we still print a short summary so the run isn't silent.
                var consoleWriter = new ConsoleOutputWriter();
                await consoleWriter.WriteAsync(result, null, cts.Token);

                foreach (var writer in writers)
                {
                    var destination = writer.FormatName switch
                    {
                        "json" => options.JsonOutputPath,
                        "csv" => options.CsvOutputPath,
                        _ => null
                    };

                    await writer.WriteAsync(result, destination, cts.Token);
                    Console.WriteLine($"Wrote {writer.FormatName.ToUpperInvariant()} output to: {destination}");
                }

                var totalDiagnostics = result.Projects.Sum(p => p.Diagnostics.Count);
                if (totalDiagnostics > 0)
                {
                    await Console.Error.WriteLineAsync($"Completed with {totalDiagnostics} diagnostic(s). Re-run with --verbose for details.");
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                await Console.Error.WriteLineAsync("Analysis canceled.");
                return 130;
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync($"Error: {ex.Message}");
                if (ex.Data.Contains("Diagnostics") && ex.Data["Diagnostics"] is List<string> diags)
                {
                    foreach (var d in diags)
                    {
                        await Console.Error.WriteLineAsync($"  - {d}");
                    }
                }
                return 1;
            }

            static void PrintUsage()
            {
                Console.WriteLine("""
    SessionWalker - Roslyn-based ASP.NET Session usage analyzer

    Usage:
      SessionWalker analyze <Solution.sln|Project.csproj> [options]
      SessionWalker list-analyzers

    Options:
      --session            Only report actions/operations that touch Session
      --session-write      Only report Session WRITE/mutation operations
      --project <name>     Restrict analysis to a single project by name
      --controller-only    Only report actions belonging to MVC controllers
      --json <path>        Write JSON output to <path>
      --csv <path>         Write CSV output to <path>
      --verbose            Print project load progress and extra diagnostics
    """);
            }

        }
    }
}
