using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>Stable failure categories produced by the loaded-driver readiness gate.</summary>
public enum InternalDriverLoadedReadinessIssue
{
    StagedBuildIdentityMissing = 0,
    ActiveHmdInvalid = 1,
    DisallowedSteamVrDevice = 2,
    RequiredControllerMissing = 3,
    DuplicateControllerIdentity = 4,
    ControllerDisconnected = 5,
    ControllerCategoryMismatch = 6,
    ControllerRoleMismatch = 7,
    ControllerMetadataMissing = 8,
    DriverIdMismatch = 9,
    TrackingSystemMismatch = 10,
    ControllerTypeMismatch = 11,
    InputProfileMismatch = 12,
    RuntimeBuildIdentityMissing = 13,
    RuntimeBuildIdentityMismatch = 14,
    AdditionalLtbDevice = 15,
    HmdCountMismatch = 16,
    InputControllerCountMismatch = 17,
    AdditionalHmd = 18,
    AdditionalInputController = 19,
}

/// <summary>One actionable reason that the loaded internal driver is not ready.</summary>
public sealed record InternalDriverLoadedReadinessDiagnostic(
    InternalDriverLoadedReadinessIssue Issue,
    string Message,
    string? DeviceSerial = null);

/// <summary>Pure readiness result for the staged build and observed SteamVR topology.</summary>
public sealed record InternalDriverLoadedReadinessResult
{
    internal InternalDriverLoadedReadinessResult(
        bool isReady,
        string successMessage,
        IEnumerable<InternalDriverLoadedReadinessDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(successMessage);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IsReady = isReady;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Diagnostic = IsReady
            ? successMessage
            : string.Join(" ", Diagnostics.Select(item => item.Message));
    }

    public bool IsReady { get; }

    public IReadOnlyList<InternalDriverLoadedReadinessDiagnostic> Diagnostics { get; }

    /// <summary>Single display-ready summary of all failures, or the success evidence.</summary>
    public string Diagnostic { get; }
}

/// <summary>
/// Compares the staged first-party driver identity with a point-in-time,
/// runtime-neutral SteamVR enumeration. It performs no I/O and does not start
/// calibration, the driver feed, or a UI.
/// </summary>
public static class InternalDriverLoadedReadiness
{
    public const string LeftControllerSerial = "LTB-TOUCH-LEFT";
    public const string RightControllerSerial = "LTB-TOUCH-RIGHT";
    public const string DriverId = "ltb";
    public const string TrackingSystemName = "ltb";
    public const string ControllerType = "ltb_touch";
    public const string InputProfilePath = "{ltb}/input/ltb_touch_profile.json";

    private static readonly (string SearchTerm, string DisplayName)[] DisallowedRuntimeTerms =
    [
        ("meta", "Meta"),
        ("quest", "Quest"),
        ("oculus", "Oculus"),
        ("alvr", "ALVR"),
    ];

    public static InternalDriverLoadedReadinessResult Evaluate(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        string? stagedBuildIdentity)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var diagnostics = new List<InternalDriverLoadedReadinessDiagnostic>();
        if (string.IsNullOrWhiteSpace(stagedBuildIdentity))
        {
            diagnostics.Add(new(
                InternalDriverLoadedReadinessIssue.StagedBuildIdentityMissing,
                "The staged driver build identity is missing. Re-stage driver_ltb with a " +
                "nonblank build-id.txt marker before restarting SteamVR."));
        }

        var hmdReadiness = ActiveHmdReadiness.Evaluate(devices);
        if (!hmdReadiness.IsReady)
        {
            diagnostics.Add(new(
                InternalDriverLoadedReadinessIssue.ActiveHmdInvalid,
                hmdReadiness.Diagnostic));
        }

        AddHmdTopologyDiagnostics(devices, diagnostics);
        AddInputControllerTopologyDiagnostics(devices, diagnostics);
        AddDisallowedDeviceDiagnostics(devices, diagnostics);
        EvaluateRequiredController(
            devices,
            LeftControllerSerial,
            SteamVrControllerRole.LeftHand,
            stagedBuildIdentity,
            diagnostics);
        EvaluateRequiredController(
            devices,
            RightControllerSerial,
            SteamVrControllerRole.RightHand,
            stagedBuildIdentity,
            diagnostics);
        AddAdditionalLtbDeviceDiagnostics(devices, diagnostics);

        return new InternalDriverLoadedReadinessResult(
            diagnostics.Count == 0,
            $"SteamVR loaded driver_ltb build '{stagedBuildIdentity}' as exactly the " +
            $"connected {LeftControllerSerial} and {RightControllerSerial} controllers, " +
            "with the intended Lighthouse HMD active at OpenVR index 0.",
            diagnostics);
    }

    private static void EvaluateRequiredController(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        string expectedSerial,
        SteamVrControllerRole expectedRole,
        string? stagedBuildIdentity,
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics)
    {
        var matches = devices
            .Where(device => string.Equals(
                device.Identity.SerialNumber,
                expectedSerial,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.RequiredControllerMissing,
                expectedSerial,
                $"SteamVR did not enumerate required controller '{expectedSerial}'. " +
                "Verify the staged driver registration, restart SteamVR, and retry.");
            return;
        }

        if (matches.Length != 1)
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.DuplicateControllerIdentity,
                expectedSerial,
                $"SteamVR enumerated {matches.Length} devices with required serial " +
                $"'{expectedSerial}'. Remove stale or duplicate driver registrations, " +
                "restart SteamVR, and retry.");
            return;
        }

        var device = matches[0];
        if (!device.IsConnected)
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.ControllerDisconnected,
                expectedSerial,
                $"Required controller '{expectedSerial}' is disconnected. Restart SteamVR " +
                "after registering the staged driver and confirm the device reconnects.");
        }

        if (device.Category != SteamVrDeviceCategory.InputController)
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.ControllerCategoryMismatch,
                expectedSerial,
                $"Device '{expectedSerial}' reports class '{device.Category}', not " +
                "InputController. Reinstall the staged driver, restart SteamVR, and retry.");
        }

        if (device.ControllerRole != expectedRole)
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.ControllerRoleMismatch,
                expectedSerial,
                $"Controller '{expectedSerial}' reports role '{device.ControllerRole}', not " +
                $"the required '{expectedRole}' role. Reinstall the staged driver and " +
                "restart SteamVR.");
        }

        if (device.Metadata is not { } metadata)
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.ControllerMetadataMissing,
                expectedSerial,
                $"Controller '{expectedSerial}' has no runtime driver metadata. Restart " +
                "SteamVR and verify that the staged driver loaded successfully.");
            return;
        }

        RequireExact(
            diagnostics,
            expectedSerial,
            InternalDriverLoadedReadinessIssue.DriverIdMismatch,
            "driver id",
            DriverId,
            metadata.DriverId);
        RequireExact(
            diagnostics,
            expectedSerial,
            InternalDriverLoadedReadinessIssue.TrackingSystemMismatch,
            "tracking system",
            TrackingSystemName,
            metadata.TrackingSystemName);
        RequireExact(
            diagnostics,
            expectedSerial,
            InternalDriverLoadedReadinessIssue.ControllerTypeMismatch,
            "controller type",
            ControllerType,
            metadata.ControllerType);
        RequireExact(
            diagnostics,
            expectedSerial,
            InternalDriverLoadedReadinessIssue.InputProfileMismatch,
            "input profile",
            InputProfilePath,
            metadata.InputProfilePath);

        if (string.IsNullOrWhiteSpace(metadata.DriverVersion))
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.RuntimeBuildIdentityMissing,
                expectedSerial,
                $"Controller '{expectedSerial}' did not report a nonblank runtime driver " +
                "build identity. Restart SteamVR after installing the staged driver build.");
        }
        else if (!string.IsNullOrWhiteSpace(stagedBuildIdentity) &&
                 !string.Equals(
                     metadata.DriverVersion,
                     stagedBuildIdentity,
                     StringComparison.Ordinal))
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.RuntimeBuildIdentityMismatch,
                expectedSerial,
                $"Controller '{expectedSerial}' is loaded from driver build " +
                $"'{metadata.DriverVersion}', but the staged build marker is " +
                $"'{stagedBuildIdentity}'. Restart SteamVR so it loads the staged build.");
        }
    }

    private static void RequireExact(
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics,
        string serial,
        InternalDriverLoadedReadinessIssue issue,
        string propertyName,
        string expected,
        string? observed)
    {
        if (string.Equals(expected, observed, StringComparison.Ordinal))
        {
            return;
        }

        Add(
            diagnostics,
            issue,
            serial,
            $"Controller '{serial}' reports {propertyName} '{Format(observed)}', not the " +
            $"required exact value '{expected}'. Reinstall the staged driver and restart " +
            "SteamVR.");
    }

    private static void AddDisallowedDeviceDiagnostics(
        IEnumerable<SteamVrDeviceDescriptor> devices,
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics)
    {
        foreach (var device in devices)
        {
            if (!TryFindDisallowedEvidence(device, out var runtimeName, out var fieldName))
            {
                continue;
            }

            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.DisallowedSteamVrDevice,
                device.Identity.SerialNumber,
                $"SteamVR {device.Category} '{device.Identity.SerialNumber}' reports " +
                $"disallowed {runtimeName} evidence in {fieldName}. Remove or disable that " +
                "Meta/Quest/Oculus/ALVR SteamVR device path, restart SteamVR, and retry.");
        }
    }

    private static void AddHmdTopologyDiagnostics(
        IEnumerable<SteamVrDeviceDescriptor> devices,
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics)
    {
        var hmds = devices
            .Where(device => device.Category == SteamVrDeviceCategory.HeadMountedDisplay)
            .ToArray();
        if (hmds.Length != 1)
        {
            diagnostics.Add(new(
                InternalDriverLoadedReadinessIssue.HmdCountMismatch,
                hmds.Length == 0
                    ? "SteamVR enumerated no HMD device. Connect and enable the intended " +
                      "Lighthouse HMD as the sole HMD at OpenVR index 0, restart SteamVR, " +
                      "and retry."
                    : $"SteamVR enumerated {hmds.Length} HMD devices, but the intended " +
                      "Lighthouse HMD must be the sole HMD at OpenVR index 0. Remove or " +
                      "disable every additional HMD device, restart SteamVR, and retry."));
        }

        if (hmds.Length <= 1)
        {
            return;
        }

        var permittedIndex = Array.FindIndex(
            hmds,
            device => device.TransientDeviceIndex == ActiveHmdReadiness.ActiveDisplayHmdIndex);
        for (var index = 0; index < hmds.Length; index++)
        {
            if (index == permittedIndex)
            {
                continue;
            }

            var device = hmds[index];
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.AdditionalHmd,
                device.Identity.SerialNumber,
                $"SteamVR enumerated additional HMD '{device.Identity.SerialNumber}' at " +
                $"OpenVR index {device.TransientDeviceIndex}. The intended Lighthouse HMD " +
                "must be the sole HMD at index 0; remove or disable the additional HMD, " +
                "restart SteamVR, and retry.");
        }
    }

    private static void AddInputControllerTopologyDiagnostics(
        IEnumerable<SteamVrDeviceDescriptor> devices,
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics)
    {
        var inputControllers = devices
            .Where(device => device.Category == SteamVrDeviceCategory.InputController)
            .ToArray();
        if (inputControllers.Length != 2)
        {
            diagnostics.Add(new(
                InternalDriverLoadedReadinessIssue.InputControllerCountMismatch,
                inputControllers.Length < 2
                    ? $"SteamVR enumerated {inputControllers.Length} InputController devices, " +
                      $"but exactly '{LeftControllerSerial}' and '{RightControllerSerial}' " +
                      "are required. Verify the staged driver registration, restart SteamVR, " +
                      "and retry."
                    : $"SteamVR enumerated {inputControllers.Length} InputController devices, " +
                      $"but exactly '{LeftControllerSerial}' and '{RightControllerSerial}' " +
                      "are required. Remove stale or unrelated controller devices, restart " +
                      "SteamVR, and retry."));
        }

        foreach (var device in inputControllers.Where(device =>
                     !IsRequiredSerial(device.Identity.SerialNumber)))
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.AdditionalInputController,
                device.Identity.SerialNumber,
                $"SteamVR enumerated additional InputController " +
                $"'{device.Identity.SerialNumber}'. Only '{LeftControllerSerial}' and " +
                $"'{RightControllerSerial}' may be present; remove or disable the additional " +
                "controller, restart SteamVR, and retry.");
        }
    }

    private static bool TryFindDisallowedEvidence(
        SteamVrDeviceDescriptor device,
        out string runtimeName,
        out string fieldName)
    {
        var evidence = new (string FieldName, string? Value)[]
        {
            ("serial number", device.Identity.SerialNumber),
            ("device path", device.Identity.DevicePath),
            ("driver id", device.Metadata?.DriverId),
            ("tracking system", device.Metadata?.TrackingSystemName),
            ("manufacturer", device.Metadata?.ManufacturerName),
            ("model", device.Metadata?.ModelNumber),
            ("controller type", device.Metadata?.ControllerType),
            ("input profile", device.Metadata?.InputProfilePath),
            ("driver version", device.Metadata?.DriverVersion),
        };
        foreach (var (observedField, value) in evidence)
        {
            foreach (var (searchTerm, displayName) in DisallowedRuntimeTerms)
            {
                if (searchTerm == "meta" &&
                    observedField == "manufacturer" &&
                    string.Equals(value, "Meta-Develop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (value?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                runtimeName = displayName;
                fieldName = observedField;
                return true;
            }
        }

        runtimeName = string.Empty;
        fieldName = string.Empty;
        return false;
    }

    private static void AddAdditionalLtbDeviceDiagnostics(
        IEnumerable<SteamVrDeviceDescriptor> devices,
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics)
    {
        foreach (var device in devices.Where(device =>
                     !IsRequiredSerial(device.Identity.SerialNumber) &&
                     HasLtbEvidence(device)))
        {
            Add(
                diagnostics,
                InternalDriverLoadedReadinessIssue.AdditionalLtbDevice,
                device.Identity.SerialNumber,
                $"SteamVR enumerated additional LTB device '{device.Identity.SerialNumber}' " +
                $"with class '{device.Category}'. driver_ltb must expose only " +
                $"'{LeftControllerSerial}' and '{RightControllerSerial}'; remove the stale " +
                "device or registration and restart SteamVR.");
        }
    }

    private static bool HasLtbEvidence(SteamVrDeviceDescriptor device) =>
        device.Identity.SerialNumber.StartsWith("LTB-", StringComparison.OrdinalIgnoreCase) ||
        HasPathSegment(device.Identity.DevicePath, DriverId) ||
        EqualsIgnoreCase(device.Metadata?.DriverId, DriverId) ||
        EqualsIgnoreCase(device.Metadata?.TrackingSystemName, TrackingSystemName) ||
        EqualsIgnoreCase(device.Metadata?.ControllerType, ControllerType) ||
        EqualsIgnoreCase(device.Metadata?.InputProfilePath, InputProfilePath);

    private static bool IsRequiredSerial(string serial) =>
        string.Equals(serial, LeftControllerSerial, StringComparison.Ordinal) ||
        string.Equals(serial, RightControllerSerial, StringComparison.Ordinal);

    private static bool HasPathSegment(string path, string segment)
    {
        var normalized = path.Replace('\\', '/');
        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool EqualsIgnoreCase(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void Add(
        ICollection<InternalDriverLoadedReadinessDiagnostic> diagnostics,
        InternalDriverLoadedReadinessIssue issue,
        string serial,
        string message) => diagnostics.Add(new(issue, message, serial));

    private static string Format(string? value) => value ?? "unavailable";
}
