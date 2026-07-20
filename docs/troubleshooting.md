# Legacy ALVR/VMT Troubleshooting Reference

> [!IMPORTANT]
> This document preserves legacy ALVR, VMT, and SteamVR `TrackingOverrides`
> migration diagnostics for paths that run only behind warning-gated
> `legacy-*` commands. It does not troubleshoot the
> supported first-party desktop **Start** path. Use
> [First-party internal driver operations](internal-drivers.md) for current
> readiness and fail-safe behavior, and keep live results in the
> [Windows internal-driver checklist](windows-internal-driver-verification.md).

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

For live first-run capture, use the exact production `wizard` command from
[setup.md](setup.md#run-the-production-two-hand-wizard). For the two-hand
later-run path, use `daily`. Both require the profile store, two distinct VMT
slots from `0` through `57`, and the explicit `steamvr.vrsettings` path.

### Stuck at `DependencyCheck`

Read the structured dependency event. LTB detects but does not install
SteamVR, ALVR, or VMT. Install, enable, and configure missing third-party
components separately, then restart the affected runtime. Do not expect LTB to
change driver or firmware installation state.

For `wizard` or `daily`, verify that `http://127.0.0.1:8082/api/version` returns
a successful, nonempty response on the same Windows account. If ALVR uses a
customized dashboard web-server port, restore the default port `8082` and
restart ALVR.
Version 0.1 has no CLI option for another ALVR dashboard port. The probe is
loopback-only, has a 500 ms request bound, and is capped at 1 Hz; repeated
`DependencyUnavailable` events should be fixed at ALVR rather than by raising
the monitor rate.

If the diagnostic mentions the active display HMD, inspect the connected
`HeadMountedDisplay` at transient OpenVR index `0`. LTB rejects
Quest/ALVR/Meta/Oculus evidence and fails closed when driver or tracking-system
metadata does not positively establish Lighthouse. Configure ALVR in tracking-
reference-only mode, make the intended Lighthouse HMD the active SteamVR
display, restart SteamVR, and retry. Changing a saved profile or an HMD display
name cannot satisfy this dependency gate.

### Waiting at `WaitingForSteamVR`

Start SteamVR with the intended Lighthouse HMD. If SteamVR was restarted after
VMT installation or mode selection, wait until device enumeration has settled.
No profile or override should be considered active in this state.

If SteamVR stopped while LTB was active, the coordinator enters `SafeDisable`
before `Stopped`. Review any cleanup-failure event before restarting. A stopped
runtime is not proof that a persistent `TrackingOverrides` entry was removed.

### Waiting at `WaitingForDevices`

Run the `devices` command locally. Its output contains raw stable serials and
registered device paths, so inspect and compare them on the machine; redact
both fields before sharing any transcript. Use the readiness diagnostic and,
when needed, a locally inspected SteamVR property report to review inferred
capabilities and any input-profile path. Redact that report before sharing.
Compare the observed controller runtime/model and exact serial constraints
with the saved profiles. Common causes are:

- the physical pose source is powered off, disconnected, lacks position, is
  not eligible as a physical source, or is not fully tracked;
- ALVR is not exposing the intended Meta Touch family and role, or a reported
  input-profile path is incompatible with that family;
- the only apparent tracker candidate is a VMT virtual device, which is
  intentionally excluded from physical-source selection;
- the VMT Alive heartbeat is unavailable;
- a saved serial or semantic hand no longer matches the physical mount; or
- a device reconnected with a new transient OpenVR index and enumeration has
  not yet stabilized.

The production `daily` gate also reads the current OpenVR properties. It needs
exactly one recognized Meta Touch controller per hand. A central classifier
uses current category, role, driver metadata, controller family, and input
profile; the application does not keep a separate allowlist of model strings.
Stored `controller_runtime` or `controller_model` values do not substitute for
these current observations. During device readiness, a missing or unsupported
controller or ineligible physical source reports `DevicesUnavailable`, while
a missing or stale VMT Alive heartbeat reports `VmtUnavailable`. During the
earlier dependency loop, a VMT recovery can emit
`DependencyUnavailable` with an explicitly VMT-specific message. Diagnose the
code and message before changing VMT or controller configuration.

Reconnect is keyed by stable serial, never by transient index. LTB must pass
the normal `Ready -> ApplyProfile -> Active` gates after reacquisition; it does
not keep a stale virtual hand active while waiting.

### A supported-looking device is rejected

Do not make the display name resemble a supported product or edit the stored
`controller_model`. LTB matches current capabilities, not presentation text.
For a Meta Touch controller, inspect the left/right role, controller family,
runtime classification, and any reported input-profile path. For a Lighthouse
pose source, inspect connectivity, position capability, physical-source
eligibility, exact serial, and registered path. `/devices/vmt/...` is virtual
and cannot be used as the physical source even when SteamVR classifies it as
`GenericTracker`.

A connected HMD descriptor is not filtered by a manufacturer/model allowlist,
and LTB does not choose the active display. The `wizard` and `daily` dependency
gate instead requires the connected device at OpenVR index `0` to be an HMD
with positive Lighthouse driver or tracking-system evidence. If a newly named
HMD, controller, or tracker has no completed hardware row in
[windows-verification.md](windows-verification.md), treat the combination as
unverified rather than diagnosing descriptor acceptance as live compatibility.

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
silently replaced by a failed capture. In the production wizard, existing hand
overrides were released before capture and remain released after this failure;
the stored profile surviving on disk does not imply that its mapping is active.

### Original Touch motion is missing during capture

Stop the attempt and inspect the structured state sequence. `OverrideRelease`
must complete before `Recording`; otherwise the wizard must not treat the
observed pose as original Touch evidence. Confirm ALVR is exposing both Touch
roles, the original hand mappings are absent from the selected settings file,
and Quest cameras can observe the controllers when position is needed. Do not
manually activate another mapping during capture.

### Full 6DoF falls back to rotation-only

Keep the Meta headset cameras able to observe the selected Touch controller and
add moderate translation to multi-axis rotation. If position is unavailable or
translation remains unobservable, rotation-only is the correct safe result. It
stores exactly zero translation and a machine-readable reason.

### A stored profile is rejected

Check schema version, hand, exact tracker serial, controller identity when
present, transform convention, finite numbers, and normalized quaternion
`XYZW`. A malformed store stops safely. A recognized incompatible schema or
changed mount routes to recalibration rather than applying an unknown transform.

## Active-use failures and reconnect

### Tracker or Touch disconnects

The expected sequence is `Active -> SafeDisable -> WaitingForDevices`. LTB
first releases and verifies mappings that reference each application source or
target its semantic hand, then disables the corresponding two-hand VMT profile.
A mapping-release failure leaves that source running and makes cleanup
incomplete. The one-hand `bridge` instead retains its legacy VMT-first order.
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
also terminal if it occurs while `wizard` or `daily` is recovering from VMT or
device loss.
After an OpenVR session has been acquired, the current invocation never reopens
it or reapplies profiles; successful bounded cleanup returns exit code `3`.
Start a new SteamVR session, allow device identities to settle, and rerun the
same command. Never assume a prior transient device index is valid after
restart.

### A virtual hand appears frozen

Stop interaction immediately and keep the setup non-worn. Request a managed
shutdown. For `wizard` or `daily`, confirm exact mapping release precedes the
corresponding VMT deactivation; for `bridge`, confirm its legacy VMT-first
sequence completes. If LTB cannot report successful cleanup, inspect every
selected VMT slot and every `TrackingOverrides` entry that references an
application source or targets its semantic hand before reusing either hand. Do
not remove unrelated mappings or infer safety from a SteamVR restart alone.

## SafeDisable and rollback

### Exit code `4` or a cleanup-failure event

For active production two-hand `wizard` and `daily` cleanup, LTB first releases
or rolls back and verifies all mappings that reference the configured or
discovered application source or target its semantic hand. It deactivates that
VMT source only after the bounded settings operation succeeds. If mapping
release fails or times out, LTB intentionally leaves the affected source
running so a surviving override does not immediately point at a stale source.
It records the failure, skips deactivation for that source, and continues
independent cleanup for the other hand.

The production wizard's pre-capture `OverrideRelease` is stricter: it attempts
the source/semantic-hand release for both hands before any selected VMT source
is deactivated. If either release fails, it leaves both sources running and
does not start recording. Exit code `4` means at least one operation could not
be confirmed or an application rollback failed; it does not claim that the
final state is safe.

Keep the setup non-worn and preserve redacted evidence. Do not manually disable
an affected VMT source while SteamVR may still retain its mapping. Inspect both
surfaces, release or recover only the reviewed relevant mappings, then verify
the source can be disabled safely. Include both mappings that reference the
application source and mappings that target its semantic hand in that review.
Stop SteamVR if needed to establish a safe runtime boundary, and use the
reviewed manual recovery in
[setup.md](setup.md). Do not start another active session until no unreviewed
mapping remains.

The legacy one-hand `bridge` retains its VMT-first cleanup order and still
attempts exact settings release after a deactivation failure. Interpret cleanup
messages in the context of the command that produced them.

### Two-hand application fails partway through

The production wizard and daily-use coordinator treat a two-hand apply as one
transaction. If either hand fails, they roll back effects created by that
attempt and do not report `Active`. A rollback failure is surfaced as a
structured error and requires manual inspection of both hands. Never assume
the first hand was removed only because the second hand failed.

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

Each `wizard`, `daily`, or `wizard-demo` event has a stable event code,
severity, state, message, and UTC timestamp, with optional affected-hand,
dependency, or wizard context.
Enable the local event file with:

```text
Ltb.App.exe wizard --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> --log <events.jsonl>
Ltb.App.exe daily --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> --log <events.jsonl>
Ltb.App.exe wizard-demo --profiles <profile-store.json> --log <events.jsonl>
```

All three commands use the same sink. `--log` appends one JSON object per event and
creates the parent directory when needed. Omitting it disables the JSONL sink;
it does not create a default log. Wizard events distinguish
`NoPositionAvailable`, `PoorTranslationObservability`, and
`BadRotationCalibration`. A write failure is intentionally unable to alter
calibration or block SafeDisable or rollback, so also inspect the console result
and exit code.

### A `RuntimeFailure` event appears

`RuntimeFailure` means an unexpected live-adapter exception escaped its
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
