using HistoricalData;
using HistoricalData.Commands;

namespace HistoricalData.Tests;

/// <summary>
/// Layer 1 routing tests: confirm the dispatcher's verb table maps each
/// known subcommand to the right ICommand and rejects everything else.
/// These tests don't invoke RunAsync (which would hit the network/disk);
/// they only assert the routing decision.
/// </summary>
public sealed class SubcommandRoutingTests
{
    [Theory]
    [InlineData("cache", "audit", typeof(CacheAuditCommand))]
    [InlineData("cache", "update", typeof(CacheUpdateCommand))]
    [InlineData("cache", "catchup", typeof(CacheCatchupCommand))]
    [InlineData("cache", "discover", typeof(CacheDiscoverCommand))]
    [InlineData("cache", "verify", typeof(CacheVerifyCommand))]
    [InlineData("cache", "repair", typeof(CacheRepairCommand))]
    [InlineData("cache", "cleanup", typeof(CacheCleanupCommand))]
    [InlineData("cache", "size", typeof(CacheSizeCommand))]
    [InlineData("cache", "add-symbol", typeof(CacheAddSymbolCommand))]
    [InlineData("cache", "remove-symbol", typeof(CacheRemoveSymbolCommand))]
    [InlineData("export", "bars", typeof(ExportBarsCommand))]
    [InlineData("export", "ticks", typeof(ExportTicksCommand))]
    public void ResolveCommand_KnownVerbs_ReturnsMatchingCommand(string verb1, string verb2, Type expected)
    {
        var args = new[] { verb1, verb2, "--instrument", "EURUSD" };
        var command = Program.ResolveCommand(args);
        Assert.NotNull(command);
        Assert.IsType(expected, command);
    }

    [Theory]
    [InlineData("CACHE", "AUDIT")]
    [InlineData("Cache", "Update")]
    [InlineData("Export", "Bars")]
    public void ResolveCommand_VerbsAreCaseInsensitive(string verb1, string verb2)
    {
        var args = new[] { verb1, verb2 };
        var command = Program.ResolveCommand(args);
        Assert.NotNull(command);
    }

    [Theory]
    [InlineData("cache", "unknown-verb")]
    [InlineData("export", "unknown-verb")]
    [InlineData("unknown", "audit")]
    [InlineData("just-one-arg")]
    public void ResolveCommand_UnknownVerbs_ReturnsNull(params string[] args)
    {
        Assert.Null(Program.ResolveCommand(args));
    }

    [Fact]
    public void ResolveCommand_EmptyArgs_ReturnsNull()
    {
        Assert.Null(Program.ResolveCommand(Array.Empty<string>()));
    }

    [Fact]
    public void ResolveCommand_LeadingFlag_ReturnsNull()
    {
        // Legacy flat-flag invocations like `--instrument EURUSD` must not be
        // mistaken for a verb, so the dispatcher falls through to legacy parsing.
        var args = new[] { "--instrument", "EURUSD", "--audit" };
        Assert.Null(Program.ResolveCommand(args));
    }

    [Fact]
    public void AllCommands_ImplementICommand()
    {
        // Sanity: the subcommand classes really do implement ICommand.
        Assert.IsAssignableFrom<ICommand>(new CacheAuditCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheUpdateCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheCatchupCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheDiscoverCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheVerifyCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheRepairCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheCleanupCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheSizeCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheAddSymbolCommand());
        Assert.IsAssignableFrom<ICommand>(new CacheRemoveSymbolCommand());
        Assert.IsAssignableFrom<ICommand>(new ExportBarsCommand());
        Assert.IsAssignableFrom<ICommand>(new ExportTicksCommand());
    }
}
