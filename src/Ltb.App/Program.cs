using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Ltb.Core;
using Ltb.OpenVr;
using Ltb.Vmt;

namespace Ltb.App;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        if (!AppCommandLineOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            AppCommandLineOptions.PrintUsage(Console.Error);
            return 1;
        }

        if (options.ShowHelp)
        {
            AppCommandLineOptions.PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            using var session = OpenVrSession.Open();
            if (options.Command == AppCommand.Bridge)
            {
                return await BridgeAsync(session, options).ConfigureAwait(false);
            }

            return options.Command switch
            {
                AppCommand.Devices => PrintDevices(session),
                AppCommand.Record => Record(session, options),
                _ => throw new InvalidOperationException("Unsupported application command."),
            };
        }
        catch (OpenVrUnavailableException exception)
        {
            Console.Error.WriteLine(
                $"SteamVR/OpenVR is unavailable ({exception.Reason}): {exception.Message}");
            return 2;
        }
        catch (OneHandBridgeRunException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.SafeDisableFailures.Count > 0 ? 4 : 2;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            TimeoutException)
        {
            Console.Error.WriteLine($"Lighthouse Touch Bridge failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> BridgeAsync(
        OpenVrSession session,
        AppCommandLineOptions options)
    {
        var profile = BridgeProfileLoader.Load(options.ProfilePath!);
        var staleAfter = TimeSpan.FromSeconds(options.StaleAfterSeconds);
        UdpVmtDatagramTransport transport;
        try
        {
            transport = new UdpVmtDatagramTransport(
                new IPEndPoint(IPAddress.Loopback, UdpVmtDatagramTransport.DefaultDriverPort),
                new IPEndPoint(IPAddress.Loopback, UdpVmtDatagramTransport.DefaultResponsePort));
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"Cannot bind VMT response port {UdpVmtDatagramTransport.DefaultResponsePort} on loopback. " +
                "Close VMT Manager or the other response-port owner, then retry.",
                exception);
        }

        var client = new VmtClient(transport, OneHandBridgeCoordinator.VmtHeartbeatTimeout);
        await using var vmt = new VmtClientOneHandBridgeController(client);
        var coordinator = new OneHandBridgeCoordinator(
            new OpenVrOneHandBridgeRuntime(session),
            vmt,
            new SteamVrOneHandBridgeOverrideController(
                new SteamVrSettingsManager(options.SteamVrSettingsPath!)),
            new DeferredWindowsVerificationProbe(),
            new SystemOneHandBridgeDelay(),
            active =>
            {
                Console.WriteLine($"tracker_device_path: {active.Tracker.Identity.DevicePath}");
                Console.WriteLine($"controller_serial: {active.Controller.StableDeviceId}");
                Console.WriteLine($"vmt_device_path: {active.VmtDevice.Identity.DevicePath}");
                Console.WriteLine($"verification: {active.Verification.Summary}");
                Console.WriteLine($"effective_monitor_rate_hz: {1d / active.EffectiveMonitorInterval.TotalSeconds:R}");
                Console.WriteLine("state: active");
            });

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            Console.WriteLine("Lighthouse Touch Bridge - One-Hand Live Bridge");
            Console.WriteLine($"profile: {profile.ProfileName}");
            Console.WriteLine($"hand: {profile.Hand.ToString().ToLowerInvariant()}");
            Console.WriteLine($"tracker_serial: {profile.TrackerSerial}");
            Console.WriteLine($"vmt_slot: {options.VmtSlot}");
            Console.WriteLine($"steamvr_settings: {Path.GetFullPath(options.SteamVrSettingsPath!)}");
            Console.WriteLine("state: starting");

            var result = await coordinator.RunAsync(
                profile,
                new VmtDeviceAddress(options.VmtSlot),
                staleAfter,
                options.MonitorRateHz,
                stop.Token).ConfigureAwait(false);

            Console.WriteLine($"state: {result.StopReason}");
            Console.WriteLine($"safe_disable_failures: {result.SafeDisableFailures.Count}");
            Console.WriteLine($"message: {result.Message}");
            foreach (var failure in result.SafeDisableFailures)
            {
                Console.Error.WriteLine($"SafeDisable failure: {failure.Message}");
            }

            if (result.SafeDisableFailures.Count > 0)
            {
                return 4;
            }

            return result.StopReason == OneHandBridgeStopReason.HealthFailure ? 3 : 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static int PrintDevices(SteamVrDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateDevices();
        Console.WriteLine("Lighthouse Touch Bridge - SteamVR Devices");
        Console.WriteLine($"device_count: {devices.Count}");
        foreach (var device in devices)
        {
            Console.WriteLine();
            Console.WriteLine($"serial: {device.StableDeviceId}");
            Console.WriteLine($"  category: {device.Category}");
            Console.WriteLine($"  controller_role: {device.ControllerRole}");
            Console.WriteLine($"  connected: {device.IsConnected.ToString().ToLowerInvariant()}");
            Console.WriteLine($"  device_path: {device.Identity.DevicePath}");
            Console.WriteLine($"  transient_index: {device.TransientDeviceIndex}");
        }

        return 0;
    }

    private static int Record(OpenVrSession session, AppCommandLineOptions options)
    {
        var devices = session.EnumerateDevices();
        var trackerDevices = SelectDevices(
            devices,
            options.TrackerSerials,
            "tracker",
            SteamVrDeviceCategory.GenericTracker);
        var controllerDevices = SelectDevices(
            devices,
            options.ControllerSerials,
            "controller",
            SteamVrDeviceCategory.InputController);
        var disconnectedDevices = trackerDevices
            .Concat(controllerDevices)
            .Where(device => !device.IsConnected)
            .Select(device => device.StableDeviceId)
            .ToArray();
        if (disconnectedDevices.Length > 0)
        {
            throw new InvalidOperationException(
                $"All selected devices must be connected when recording starts; disconnected serials: {string.Join(", ", disconnectedDevices)}. Disconnects during capture remain in the recording validity metadata.");
        }

        var trackerSources = trackerDevices
            .Select(device => session.CreateTrackedPoseSource(device))
            .ToArray();
        var controllerSources = controllerDevices
            .Select(device => session.CreateInputControllerPoseSource(device))
            .ToArray();
        var capture = PoseRecordingCapture.Capture(
            trackerSources,
            controllerSources,
            options.DurationSeconds,
            options.SampleRateHz,
            new StopwatchRecordingCaptureClock());
        var recording = capture.Recording;
        using (var destination = new FileStream(
                   options.OutputPath!,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            PoseRecordingJson.Write(recording, destination);
        }

        Console.WriteLine("Lighthouse Touch Bridge - Live Recording Complete");
        Console.WriteLine($"format: {PoseRecording.FormatIdentifier}");
        Console.WriteLine($"schema_version: {recording.SchemaVersion}");
        foreach (var trackerDevice in trackerDevices)
        {
            Console.WriteLine($"tracker_serial: {trackerDevice.StableDeviceId}");
        }

        foreach (var controllerDevice in controllerDevices)
        {
            Console.WriteLine($"controller_serial: {controllerDevice.StableDeviceId}");
        }

        Console.WriteLine($"requested_duration_seconds: {options.DurationSeconds:F3}");
        Console.WriteLine($"requested_rate_hz: {options.SampleRateHz:F3}");
        Console.WriteLine("sampling_window: [0, duration)");
        Console.WriteLine($"override_released_acknowledged: {options.OverrideReleased.ToString().ToLowerInvariant()}");
        Console.WriteLine($"sampling_ticks: {capture.SamplingTicks}");
        if (trackerDevices.Count == 1 && controllerDevices.Count == 1)
        {
            Console.WriteLine($"tracker_samples: {recording.GetStream("tracker").Samples.Count}");
            Console.WriteLine($"controller_samples: {recording.GetStream("controller").Samples.Count}");
        }
        else
        {
            foreach (var stream in recording.Streams)
            {
                Console.WriteLine($"stream_samples: {stream.Identity.StreamId}={stream.Samples.Count}");
            }
        }

        Console.WriteLine($"capture_elapsed_seconds: {capture.CaptureElapsedSeconds:F3}");
        Console.WriteLine($"output: {options.OutputPath}");
        return 0;
    }

    internal static IReadOnlyList<SteamVrDeviceDescriptor> SelectDevices(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        IReadOnlyList<string> stableSerials,
        string selectorName,
        SteamVrDeviceCategory expectedCategory)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(stableSerials);
        var selected = new List<SteamVrDeviceDescriptor>(stableSerials.Count);
        foreach (var stableSerial in stableSerials)
        {
            var device = devices.SingleOrDefault(candidate =>
                string.Equals(candidate.StableDeviceId, stableSerial, StringComparison.Ordinal));
            if (device is null)
            {
                throw new ArgumentException(
                    $"No SteamVR device matches --{selectorName} serial '{stableSerial}'. Run the devices command to list stable serials.");
            }

            if (device.Category != expectedCategory)
            {
                throw new ArgumentException(
                    $"Selected {selectorName} serial '{stableSerial}' has category {device.Category}; expected {expectedCategory}.");
            }

            selected.Add(device);
        }

        return selected.AsReadOnly();
    }
}

internal sealed record AppCommandLineOptions(
    AppCommand Command,
    IReadOnlyList<string> TrackerSerials,
    IReadOnlyList<string> ControllerSerials,
    string? OutputPath,
    double DurationSeconds,
    double SampleRateHz,
    bool OverrideReleased,
    string? ProfilePath,
    int VmtSlot,
    string? SteamVrSettingsPath,
    double StaleAfterSeconds,
    double MonitorRateHz,
    bool ShowHelp)
{
    private const double MaximumDurationSeconds = 3_600d;
    private const double MaximumSampleRateHz = 1_000d;
    private const double MaximumStaleAfterSeconds = 300d;
    private const double MaximumMonitorRateHz = 1_000d;

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out AppCommandLineOptions options,
        out string? error)
    {
        options = Empty;
        error = null;
        if (arguments.Count == 1 && arguments[0] is "-h" or "--help")
        {
            options = Empty with { ShowHelp = true };
            return true;
        }

        if (arguments.Count == 0 || !TryParseCommand(arguments[0], out var command))
        {
            error = arguments.Count == 0
                ? "A devices, record, or bridge command is required."
                : $"Unknown command '{arguments[0]}'.";
            return false;
        }

        var trackerSerials = new List<string>();
        var controllerSerials = new List<string>();
        string? outputPath = null;
        var durationSeconds = 10d;
        var sampleRateHz = 90d;
        var overrideReleased = false;
        string? profilePath = null;
        var vmtSlot = -1;
        string? steamVrSettingsPath = null;
        var staleAfterSeconds = 0.5d;
        var monitorRateHz = 20d;
        var recorderOptionSpecified = false;
        var bridgeOptionSpecified = false;
        var showHelp = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;
                case "--tracker":
                    recorderOptionSpecified = true;
                    if (!TryReadValue(arguments, ref index, argument, out var trackerSerial, out error))
                    {
                        return false;
                    }

                    trackerSerials.Add(trackerSerial!);

                    break;
                case "--controller":
                    recorderOptionSpecified = true;
                    if (!TryReadValue(arguments, ref index, argument, out var controllerSerial, out error))
                    {
                        return false;
                    }

                    controllerSerials.Add(controllerSerial!);

                    break;
                case "--output":
                    recorderOptionSpecified = true;
                    if (!TryReadValue(arguments, ref index, argument, out outputPath, out error))
                    {
                        return false;
                    }

                    break;
                case "--duration":
                    recorderOptionSpecified = true;
                    if (!TryReadDouble(arguments, ref index, argument, out durationSeconds, out error))
                    {
                        return false;
                    }

                    break;
                case "--rate":
                    recorderOptionSpecified = true;
                    if (!TryReadDouble(arguments, ref index, argument, out sampleRateHz, out error))
                    {
                        return false;
                    }

                    break;
                case "--override-released":
                    recorderOptionSpecified = true;
                    overrideReleased = true;
                    break;
                case "--profile":
                    bridgeOptionSpecified = true;
                    if (!TryReadValue(arguments, ref index, argument, out profilePath, out error))
                    {
                        return false;
                    }

                    break;
                case "--vmt-slot":
                    bridgeOptionSpecified = true;
                    if (!TryReadInt32(arguments, ref index, argument, out vmtSlot, out error))
                    {
                        return false;
                    }

                    break;
                case "--steamvr-settings":
                    bridgeOptionSpecified = true;
                    if (!TryReadValue(
                            arguments,
                            ref index,
                            argument,
                            out steamVrSettingsPath,
                            out error))
                    {
                        return false;
                    }

                    break;
                case "--stale-after":
                    bridgeOptionSpecified = true;
                    if (!TryReadDouble(
                            arguments,
                            ref index,
                            argument,
                            out staleAfterSeconds,
                            out error))
                    {
                        return false;
                    }

                    break;
                case "--monitor-rate":
                    bridgeOptionSpecified = true;
                    if (!TryReadDouble(
                            arguments,
                            ref index,
                            argument,
                            out monitorRateHz,
                            out error))
                    {
                        return false;
                    }

                    break;
                default:
                    error = $"Unknown option '{argument}'.";
                    return false;
            }
        }

        if (showHelp)
        {
            options = Empty with { Command = command, ShowHelp = true };
            return true;
        }

        if (command == AppCommand.Devices)
        {
            if (recorderOptionSpecified || bridgeOptionSpecified)
            {
                error = "The devices command does not accept record or bridge options.";
                return false;
            }

            options = Empty with { Command = command };
            return true;
        }

        if (command == AppCommand.Bridge)
        {
            if (recorderOptionSpecified)
            {
                error = "The bridge command does not accept record options.";
                return false;
            }

            if (profilePath is null || vmtSlot < 0 || steamVrSettingsPath is null)
            {
                error = "The bridge command requires --profile <profile.json>, --vmt-slot <0..57>, and --steamvr-settings <steamvr.vrsettings>.";
                return false;
            }

            if (vmtSlot is < VmtDeviceAddress.MinimumIndex or > VmtDeviceAddress.MaximumIndex)
            {
                error = $"--vmt-slot must be between {VmtDeviceAddress.MinimumIndex} and {VmtDeviceAddress.MaximumIndex}.";
                return false;
            }

            if (!double.IsFinite(staleAfterSeconds) ||
                staleAfterSeconds < OneHandBridgeCoordinator.MinimumStaleAfterSeconds ||
                staleAfterSeconds > MaximumStaleAfterSeconds)
            {
                error = $"--stale-after must be at least {OneHandBridgeCoordinator.MinimumStaleAfterSeconds:R} and at most {MaximumStaleAfterSeconds:F0} seconds.";
                return false;
            }

            if (!double.IsFinite(monitorRateHz) ||
                monitorRateHz <= 0d ||
                monitorRateHz > MaximumMonitorRateHz)
            {
                error = $"--monitor-rate must be greater than zero and at most {MaximumMonitorRateHz:F0} Hz.";
                return false;
            }

            options = Empty with
            {
                Command = command,
                ProfilePath = profilePath,
                VmtSlot = vmtSlot,
                SteamVrSettingsPath = steamVrSettingsPath,
                StaleAfterSeconds = staleAfterSeconds,
                MonitorRateHz = monitorRateHz,
            };
            return true;
        }

        if (bridgeOptionSpecified)
        {
            error = "The record command does not accept bridge options.";
            return false;
        }

        if (trackerSerials.Count == 0 || controllerSerials.Count == 0 || outputPath is null)
        {
            error = "The record command requires at least one --tracker <serial>, at least one --controller <serial>, and --output <recording.json>.";
            return false;
        }

        var duplicateSerial = trackerSerials
            .Concat(controllerSerials)
            .GroupBy(serial => serial, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateSerial is not null)
        {
            error = $"Device serial '{duplicateSerial}' was selected more than once; repeat --tracker and --controller only for distinct devices.";
            return false;
        }

        if (!overrideReleased)
        {
            error = "The record command requires --override-released to acknowledge that VMT and SteamVR pose overrides are inactive.";
            return false;
        }

        if (!double.IsFinite(durationSeconds) ||
            durationSeconds <= 0d ||
            durationSeconds > MaximumDurationSeconds)
        {
            error = $"--duration must be greater than zero and at most {MaximumDurationSeconds:F0} seconds.";
            return false;
        }

        if (!double.IsFinite(sampleRateHz) ||
            sampleRateHz <= 0d ||
            sampleRateHz > MaximumSampleRateHz)
        {
            error = $"--rate must be greater than zero and at most {MaximumSampleRateHz:F0} Hz.";
            return false;
        }

        options = new AppCommandLineOptions(
            command,
            trackerSerials.AsReadOnly(),
            controllerSerials.AsReadOnly(),
            outputPath,
            durationSeconds,
            sampleRateHz,
            overrideReleased,
            null,
            -1,
            null,
            0.5d,
            20d,
            false);
        return true;
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Lighthouse Touch Bridge console utility");
        writer.WriteLine();
        writer.WriteLine("Repository-root usage:");
        writer.WriteLine("  dotnet run --project src/Ltb.App -- devices");
        writer.WriteLine("  dotnet run --project src/Ltb.App -- record --tracker <stable-serial> [--tracker <stable-serial> ...] --controller <stable-serial> [--controller <stable-serial> ...] --output <recording.json> --override-released [--duration <seconds>] [--rate <hz>]");
        writer.WriteLine("  dotnet run --project src/Ltb.App -- bridge --profile <profile.json> --vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--stale-after <seconds>] [--monitor-rate <hz>]");
        writer.WriteLine();
        writer.WriteLine("Defaults: record --duration 10 --rate 90; bridge --stale-after 0.5 --monitor-rate 20.");
        writer.WriteLine("The bridge command touches only the explicit --steamvr-settings path; it never searches for host settings.");
        writer.WriteLine("--stale-after governs reported tracker pose sample age when available; synchronous OpenVR reads still require connected, RunningOk, full-valid poses.");
        writer.WriteLine("VMT Alive heartbeat and post-activation connection use a separate conservative 5-second bound.");
        writer.WriteLine("--monitor-rate is a requested minimum rate; safety bounds automatically make the effective watchdog faster when required.");
        writer.WriteLine("Bridge exit codes: 0 cancelled+disabled, 2 startup/run failure, 3 health-triggered SafeDisable, 4 incomplete SafeDisable cleanup.");
        writer.WriteLine("--override-released explicitly acknowledges that VMT and SteamVR pose overrides are inactive, so the original controller pose is sampled.");
        writer.WriteLine("The record command does not inspect or modify SteamVR settings.");
    }

    private static AppCommandLineOptions Empty { get; } = new(
        AppCommand.Devices,
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        10d,
        90d,
        false,
        null,
        -1,
        null,
        0.5d,
        20d,
        false);

    private static bool TryParseCommand(string value, out AppCommand command)
    {
        command = value switch
        {
            "devices" => AppCommand.Devices,
            "record" => AppCommand.Record,
            "bridge" => AppCommand.Bridge,
            _ => (AppCommand)(-1),
        };
        return Enum.IsDefined(command);
    }

    private static bool TryReadDouble(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out double value,
        out string? error)
    {
        if (!TryReadValue(arguments, ref index, option, out var text, out error))
        {
            value = default;
            return false;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"Option '{option}' requires a number; received '{text}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadInt32(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out int value,
        out string? error)
    {
        if (!TryReadValue(arguments, ref index, option, out var text, out error))
        {
            value = default;
            return false;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"Option '{option}' requires an integer; received '{text}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= arguments.Count)
        {
            value = null;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        value = arguments[++index];
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Option '{option}' requires a non-empty value.";
            return false;
        }

        error = null;
        return true;
    }
}

internal enum AppCommand
{
    Devices,
    Record,
    Bridge,
}
