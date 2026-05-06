namespace HistoricalData.Commands;

/// <summary>
/// A subcommand that can be invoked from the CLI. Each command owns its
/// own argument parsing and produces an exit code (0 = success, non-zero
/// = failure). The dispatcher in <see cref="Program"/> routes to the right
/// command based on the verb in args[0..1].
///
/// Commands are intentionally thin during Layer 1 of the refactor — they
/// build an <see cref="AppOptions"/> with their preferred defaults and
/// delegate to existing engine code in <see cref="Program"/>. Layer 3
/// will decouple the engine itself.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// Execute the command with the args remaining after the verb has been
    /// consumed (e.g. for `cache audit --instrument EURUSD`, args here will
    /// be `--instrument EURUSD`).
    /// </summary>
    Task<int> RunAsync(string[] args);
}
