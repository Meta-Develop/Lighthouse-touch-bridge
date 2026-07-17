# Troubleshooting

This guide starts from the state or symptom visible to the operator. Preserve
the structured event code, application state, and redacted context when a
problem needs investigation. Do not share raw settings, backups, device
serials, recordings, or owner-local paths.

## Package does not start

### Windows asks for .NET

Use the self-contained `win-x64` ZIP produced by `build/package-win-x64.sh`.
The supported package contains the .NET runtime and does not require a separate
.NET installation. A framework-dependent output from an older development
command is not the supported Version 0.1 package.

Confirm that the extracted directory still contains `Ltb.App.exe`, the managed
assemblies, runtime files, `openvr_api.dll`, and the `licenses` directory. Do
not copy only the executable.

### The package hash or OpenVR DLL hash does not match

Do not run the package. Verify that the ZIP and `.sha256` came from the same
approved release. The expected `openvr_api.dll` SHA-256 is also recorded in
`release-manifest.txt`. Re-extracting cannot make a mismatched download
trustworthy; obtain the package again through the approved channel.

### Windows displays an unsigned-application warning

Version 0.1 produces an unsigned portable ZIP. The build does not claim code
signing or SmartScreen reputation. Confirm the ZIP checksum and distribution
channel before proceeding. Signing acceptance remains a Windows release task.

## Startup states

For the production two-hand later-run path, use the exact `daily` command from
[setup.md](setup.md). Its required arguments are the profile store, two
distinct VMT slots from `0` through `57`, and the explicit
`steamvr.vrsettings` path. The optional `--monitor-rate` and
`--reconnect-delay` values default to `20` Hz and `0.25` seconds.

### Stuck at `DependencyCheck`

Read the structured dependency event. LTB detects but does not install
SteamVR, ALVR, or VMT. Install, enable, and configure missing third-party
components separately, then restart the affected runtime. Do not expect LTB to
change driver or firmware installation state.

For `daily`, verify that `http://127.0.0.1:8082/api/version` returns a successful,
nonempty response on the same Windows account. If ALVR uses a customized
dashboard web-server port, restore the default port `8082` and restart ALVR.
Version 0.1 has no CLI option for another ALVR dashboard port. The probe is
loopback-only, has a 500 ms request bound, and is capped at 1 Hz; repeated
`DependencyUnavailable` events should be fixed at ALVR rather than by raising
the `daily` monitor rate.

### Waiting at `WaitingForSteamVR`

Start SteamVR with the intended Lighthouse HMD. If SteamVR was restarted after
VMT installation or mode selection, wait until device enumeration has settled.
No profile or override should be considered active in this state.

If SteamVR stopped while LTB was active, the coordinator enters `SafeDisable`
before `Stopped`. Review any cleanup-failure event before restarting. A stopped
runtime is not proof that a persistent `TrackingOverrides` entry was removed.

### Waiting at `WaitingForDevices`

Run the `devices` command and compare redacted stable identities, categories,
and controller roles with the saved profiles. Common causes are:

- the physical tracker is powered off, disconnected, or not fully tracked;
- ALVR is not exposing the intended Touch controller and role;
- the VMT Alive heartbeat is unavailable;
- a saved serial or semantic hand no longer matches the physical mount; or
- a device reconnected with a new transient OpenVR index and enumeration has
  not yet stabilized.

The production `daily` gate also reads the current OpenVR properties. It needs
exactly one controller per hand with driver/tracking system `oculus`,
manufacturer `Oculus`, the role-matched Miramar left/right model, and controller
type `oculus_touch`. Stored `controller_runtime` or `controller_model` values do
not substitute for these current observations. During device readiness, a
missing controller, unsupported tuple, or missing physical tracker reports
`DevicesUnavailable`, while a missing or stale VMT Alive heartbeat reports
`VmtUnavailable`. During the earlier dependency loop, a VMT recovery can emit
`DependencyUnavailable` with an explicitly VMT-specific message. Diagnose the
code and message before changing VMT or controller configuration.

Reconnect is keyed by stable serial, never by transient index. LTB must pass
the normal `Ready -> ApplyProfile -> Active` gates after reacquisition; it does
not keep a stale virtual hand active while waiting.

### VMT slot appears with the wrong device class

VMT honors device mode on the first registration of a slot in a SteamVR
process. Stop LTB, restart SteamVR, and let LTB's mode-1 (`Tracker`) Joint
command perform the first registration for that slot. A later command cannot
reclassify an already registered device in the same SteamVR process.

### LTB cannot bind VMT response port `39571`

Close VMT Manager and any other process using UDP port `39571`, then retry.
VMT Manager and LTB cannot own the response port at the same time. LTB exits
instead of activating an override without a working response path.

## Calibration and profile diagnostics

The following outcomes are intentionally distinct:

| Observation | Event code | Meaning | Result |
| --- | --- | --- | --- |
| No controller position is available | `NoPositionAvailable` | Orientation can still support mount rotation | Successful rotation-only fallback with zero translation |
| Translation motion is poorly observable | `PoorTranslationObservability` | Position exists, but the capture cannot support a reliable lever arm | Successful rotation-only fallback with zero translation |
| Rotation calibration is bad | `BadRotationCalibration` | The fixed mount rotation is not supported by the capture | Calibration failure; return to `Ready` with a retry diagnostic |
| A tracker is lost during active use | `TrackerLost` | A runtime pose source is no longer safe | `SafeDisable`, then device reacquisition by stable serial |

Do not treat the two fallback cases as errors, and do not treat bad rotation as
a translation fallback. A lost tracker is a runtime safety transition, not a
calibration-quality result.

### Calibration returns to `Ready`

Use broader pitch, yaw, and roll with valid tracking. Static motion,
single-axis motion, corrupted orientation, or ambiguous tracker association
cannot establish the mount rotation. The previous usable profile is not
silently replaced by a failed capture.

### Full 6DoF falls back to rotation-only

Keep Quest cameras able to observe Touch position and add moderate translation
to multi-axis rotation. If position is unavailable or translation remains
unobservable, rotation-only is the correct safe result. It stores exactly zero
translation and a machine-readable reason.

### A stored profile is rejected

Check schema version, hand, exact tracker serial, controller identity when
present, transform convention, finite numbers, and normalized quaternion
`XYZW`. A malformed store stops safely. A recognized incompatible schema or
changed mount routes to recalibration rather than applying an unknown transform.

## Active-use failures and reconnect

### Tracker or Touch disconnects

The expected sequence is `Active -> SafeDisable -> WaitingForDevices`. LTB
first disables every active daily-use VMT profile and releases every LTB-owned
hand mapping, then waits for the required stable serial and role. The one-hand
`bridge` command applies the same rule to its single active profile.
Reconnecting a different same-class device or reusing a transient index must
not satisfy the gate.

### ALVR local availability is lost

If the loopback `/api/version` proof fails during active use, the watchdog
reports Touch input loss and enters SafeDisable. Restore ALVR on its default
local port, then allow the normal device and current-property gates to pass;
the endpoint response alone is not enough to reapply profiles.

### VMT stops or its heartbeat becomes stale

The expected sequence is `Active -> SafeDisable`, followed by dependency or
device waiting. Verify VMT is enabled, its heartbeat path is available, the
slot still has the expected identity, and response port `39571` is free before
another apply attempt.

### SteamVR stops

The expected sequence is `Active -> SafeDisable -> Stopped`. A SteamVR stop is
also terminal if it occurs while `daily` is recovering from VMT or device loss.
After an OpenVR session has been acquired, the current invocation never reopens
it or reapplies profiles; successful bounded cleanup returns exit code `3`.
Start a new SteamVR session, allow device identities to settle, and run `daily`
again. Never assume a prior transient device index is valid after restart.

### A virtual hand appears frozen

Stop interaction immediately and keep the setup non-worn. Request a managed
shutdown and confirm both VMT deactivation and exact mapping release. If LTB
cannot report successful cleanup, inspect every selected VMT slot and each
exact LTB-owned `TrackingOverrides` entry before reusing either hand. Do not
remove unrelated mappings or infer safety from a SteamVR restart alone.

## SafeDisable and rollback

### Exit code `4` or a cleanup-failure event

LTB attempts both cleanup surfaces even if one fails: VMT deactivation first,
then exact settings release. Exit code `4` means at least one operation could
not be confirmed or an application rollback failed. Each independent cleanup
operation has a two-second bound; timing out one operation does not skip the
other. Exit code `4` does not claim that the final state is safe.

Stop LTB and SteamVR, preserve redacted evidence, inspect both surfaces, and
use the reviewed manual recovery in [setup.md](setup.md). Do not start another
active session until no unreviewed mapping remains.

### Two-hand application fails partway through

The daily-use coordinator treats a two-hand apply as one transaction. If either
hand fails, it rolls back effects created by that attempt and does not report
`Active`. A rollback failure is surfaced as a structured error and requires
manual inspection of both hands. Never assume the first hand was removed only
because the second hand failed.

### Settings write or validation fails

`SteamVrSettingsManager` uses a sibling lock, byte-exact backup,
same-directory staging file, atomic replacement, and post-write validation. It
restores the original only while it can prove LTB still owns the written bytes;
it will not overwrite a later external writer. Use only a recognized adjacent
`.ltb-backup` for reviewed recovery, and preserve the current file before
replacement.

### The process or OS terminated abruptly

An unmanaged termination can prevent cleanup code from running. The next run
attempts startup cleanup before activation, but this is best-effort recovery,
not a crash-proof guarantee. Inspect both selected VMT slots and both
LTB-owned persistent hand mappings.
Console destruction, OS crash, and power loss require the same manual review.

## Structured logs and evidence

Each `daily` or `wizard-demo` event has a stable event code, severity, state,
message, and UTC timestamp, with optional affected-hand, dependency, or wizard
context.
Enable the local event file with:

```text
Ltb.App.exe daily --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> --log <events.jsonl>
Ltb.App.exe wizard-demo --profiles <profile-store.json> --log <events.jsonl>
```

Both commands use the same sink. `--log` appends one JSON object per event and
creates the parent directory when needed. Omitting it disables the JSONL sink;
it does not create a default log. Wizard events distinguish
`NoPositionAvailable`, `PoorTranslationObservability`, and
`BadRotationCalibration`. A write failure is intentionally unable to alter
calibration or block SafeDisable or rollback, so also inspect the console result
and exit code.

### A `RuntimeFailure` event appears

`RuntimeFailure` means an unexpected daily-use adapter exception escaped its
normal typed health result. During active use, LTB records the exception type
and message before it starts bounded cleanup; it does not write a stack trace to
the structured event. Confirm that `RuntimeFailure` precedes
`SafeDisableStarted` and the final `Stopped` transition. Then verify every
active VMT and exact mapping cleanup result. Exit code `3` means bounded cleanup
reported no failure; exit code `4` means cleanup or rollback remains incomplete.

When reporting a failure, retain:

- application version and source commit from `release-manifest.txt`;
- the event code and state transition;
- Windows, SteamVR, ALVR, and VMT versions;
- the exact command with paths and identities redacted; and
- whether cleanup and rollback were reported as complete.

Do not commit or send raw logs, device serials, recordings,
`steamvr.vrsettings`, or `.ltb-backup` files. Redact owner-local paths and keep
stable token correspondence within one report, such as `TRACKER-A`. Version
0.1 has no telemetry or cloud log upload; evidence export is operator-managed.

The full deferred hardware and Windows test matrix is in
[windows-verification.md](windows-verification.md).
