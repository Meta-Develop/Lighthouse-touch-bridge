using Ltb.App;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverCommandLineTests
{
    [Fact]
    public void NoArgumentsAndRunSelectTheFirstPartyInternalDriver()
    {
        Assert.True(AppCommandLineOptions.TryParse([], out var defaults, out var defaultError), defaultError);
        Assert.Equal(AppCommand.InternalDriver, defaults.Command);
        Assert.Null(defaults.UnsupportedMigrationCommand);

        Assert.True(AppCommandLineOptions.TryParse(["run"], out var explicitRun, out var runError), runError);
        Assert.Equal(AppCommand.InternalDriver, explicitRun.Command);
        Assert.Null(explicitRun.UnsupportedMigrationCommand);
    }

    [Theory]
    [InlineData("--steamvr-settings", "steamvr.vrsettings")]
    [InlineData("--left-vmt-slot", "1")]
    [InlineData("--profiles", "profiles.json")]
    public void ProductionRunRejectsLegacySetupFields(string option, string value)
    {
        Assert.False(AppCommandLineOptions.TryParse(
            ["run", option, value],
            out _,
            out var error));
        Assert.Contains("accepts no setup options", error, StringComparison.Ordinal);
        Assert.Contains("automatic", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultExecutionUsesInjectedFactoryAndPreservesCancellation()
    {
        var session = new RecordingInternalDriverSession(InternalDriverSessionSnapshot.Initial);
        InternalDriverSessionOptions? receivedOptions = new();
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            [],
            options =>
            {
                receivedOptions = options;
                return session;
            },
            output,
            error,
            cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Null(receivedOptions);
        Assert.Equal(cancellation.Token, session.RunCancellationToken);
        Assert.Equal(1, session.RunCount);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Contains("First-Party Internal Driver", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("vmt", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task FaultedInternalSessionReturnsStartupFailure()
    {
        var fault = InternalDriverSessionSnapshot.Initial with
        {
            State = InternalDriverSessionState.Faulted,
            Diagnostic = "deterministic fault",
            Remediation = "deterministic remediation",
        };
        var session = new RecordingInternalDriverSession(fault);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["run"],
            _ => session,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("state=Faulted", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("diagnostic=deterministic fault", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [MemberData(nameof(BareLegacyMigrationCommands))]
    public async Task BareLegacyMigrationAliasesNeverReachAHandler(
        string command,
        string[] arguments)
    {
        Assert.True(AppCommandLineOptions.TryParse(arguments, out var options, out var parseError), parseError);
        Assert.Equal(command, options.UnsupportedMigrationCommand);
        var factoryCalled = false;
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            arguments,
            _ =>
            {
                factoryCalled = true;
                throw new InvalidOperationException("Migration aliases must not create a session.");
            },
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(factoryCalled);
        Assert.Contains("parse-only migration alias", error.ToString(), StringComparison.Ordinal);
        Assert.Contains($"legacy-{command}", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareLegacyAliasWithHelpIsStillRejectedBeforeDispatch()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["bridge", "--help"],
            _ => throw new InvalidOperationException("The migration alias must not dispatch."),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("parse-only migration alias", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("legacy-bridge", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HelpSeparatesZeroInputProductionFromUnsupportedLegacyCommands()
    {
        using var writer = new StringWriter();

        AppCommandLineOptions.PrintUsage(writer);

        var help = writer.ToString();
        var legacyStart = help.IndexOf(
            "Unsupported legacy compile-only commands",
            StringComparison.Ordinal);
        Assert.True(legacyStart > 0);
        var productionHelp = help[..legacyStart];
        Assert.Contains("dotnet run --project src/Ltb.App -- run", productionHelp, StringComparison.Ordinal);
        Assert.Contains("zero setup arguments", productionHelp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--left-vmt-slot", productionHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("--steamvr-settings", productionHelp, StringComparison.Ordinal);
        Assert.Contains("ALVR", help, StringComparison.Ordinal);
        Assert.Contains("VMT", help, StringComparison.Ordinal);
        Assert.Contains("TrackingOverrides", help, StringComparison.Ordinal);
        foreach (var command in new[]
                 {
                     "legacy-devices",
                     "legacy-record",
                     "legacy-bridge",
                     "legacy-daily",
                     "legacy-wizard",
                     "legacy-wizard-demo",
                 })
        {
            Assert.Contains(command, help, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, string[]> BareLegacyMigrationCommands => new()
    {
        { "devices", ["devices"] },
        {
            "record",
            [
                "record",
                "--tracker", "TRACKER",
                "--controller", "CONTROLLER",
                "--output", "recording.json",
                "--override-released",
            ]
        },
        {
            "bridge",
            [
                "bridge",
                "--profile", "profile.json",
                "--vmt-slot", "1",
                "--steamvr-settings", "steamvr.vrsettings",
            ]
        },
        {
            "daily",
            [
                "daily",
                "--profiles", "profiles.json",
                "--left-vmt-slot", "1",
                "--right-vmt-slot", "2",
                "--steamvr-settings", "steamvr.vrsettings",
            ]
        },
        {
            "wizard",
            [
                "wizard",
                "--profiles", "profiles.json",
                "--left-vmt-slot", "1",
                "--right-vmt-slot", "2",
                "--steamvr-settings", "steamvr.vrsettings",
            ]
        },
        { "wizard-demo", ["wizard-demo", "--profiles", "profiles.json"] },
    };

    private sealed class RecordingInternalDriverSession : IInternalDriverSession
    {
        public RecordingInternalDriverSession(InternalDriverSessionSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
        }

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public InternalDriverSessionSnapshot CurrentSnapshot { get; }

        public CancellationToken RunCancellationToken { get; private set; }

        public int RunCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken = default)
        {
            RunCancellationToken = cancellationToken;
            RunCount++;
            return Task.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
