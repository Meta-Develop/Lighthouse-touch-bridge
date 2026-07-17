# Setup

## Milestone 2 scope

Milestone 2 runs one saved calibration profile for one hand. It configures one
VMT device, applies the profile's tracker-to-controller mount transform, adds
one safe SteamVR `TrackingOverrides` mapping, and monitors the tracker, Touch
controller, and VMT source until cancellation or a health failure. It does not
perform calibration, association, two-hand setup, or GUI installation. The
complete product flow remains in the [project specification](specification.md).

The automated suite exercises this sequence with fake runtimes on Linux. A
real bridge requires Windows x64 and the deferred checks in
[windows-verification.md](windows-verification.md).

## Windows prerequisites

Install and prepare these components before running the command:

- the .NET 8 SDK, because the documented command uses `dotnet run`;
- SteamVR, running with the intended Lighthouse HMD;
- [Virtual Motion Tracker (VMT)](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/setup/),
  installed, enabled in SteamVR add-ons, and visible to the runtime;
- ALVR configured to expose the selected Touch controller and its inputs
  without taking over as the active HMD; and
- the physical Lighthouse tracker named by the profile, powered on, fully
  tracked, and identified by the exact live serial printed by `devices`.

VMT receives commands on loopback UDP port `39570` and sends responses to
`39571`. Close VMT Manager before starting LTB because the manager and LTB use
the same response port. If LTB cannot bind `39571`, it exits with a diagnostic
instead of starting the override. The port assignments and command fields are
defined by the [official VMT API](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/api/).

If VMT was just installed, enabled, or assigned a device mode for the first
time, complete the VMT-requested SteamVR restart before continuing. From the
repository root, inspect the live enumeration:

```powershell
dotnet run --project src/Ltb.App -- devices
```

For a live Windows run, copy the physical tracker's `serial:` value exactly
from this output into `tracker_serial`; matching is case-sensitive. If
`controller_serial` is present, it must likewise exactly match the intended
Touch controller. `LHR-TEST0001`, `CTRL-TEST0001`, and every other identifier
in this document are examples only and must not be used as live hardware
identities. Confirm the physical tracker is a connected generic tracker and
the intended Touch controller has the correct hand role.

A fresh VMT slot can be absent until LTB sends its first safe enabled Joint
configuration. LTB always requests VMT mode `1` (`Tracker`), then discovers the
slot and requires it to enumerate as `GenericTracker`. VMT honors device type
when a slot first registers in the current SteamVR process. If the selected
slot was first registered in another mode, stop LTB, restart SteamVR, and retry
so this command performs the first registration in Tracker mode. An existing
slot's device index remains transient and is not a profile key.

Choose the exact `steamvr.vrsettings` file that belongs to this SteamVR
installation. LTB intentionally does not search for it. The file and its
parent directory must allow the current user to read the file and create,
flush, rename, and exclusively open sibling lock, staging, and backup files.
Do not point the command at a copied fixture when the intent is to control the
live runtime.

## Profile schema 1

The bridge reads UTF-8 profile schema 1. The required Milestone 2 subset is
shown below with clearly fake identifiers:

```json
{
  "schema_version": 1,
  "profile_name": "Left hand test mount",
  "hand": "left",
  "controller_serial": "CTRL-TEST0001",
  "tracker_serial": "LHR-TEST0001",
  "selected_mode": "full_6dof",
  "tracker_to_controller": {
    "translation_m": [0.014, -0.052, 0.031],
    "rotation_xyzw": [0.0, 0.0, 0.0, 1.0]
  }
}
```

`hand` is `left` or `right`. `selected_mode` is `full_6dof` or
`rotation_only`; a rotation-only profile must store exactly
`[0, 0, 0]` translation. Translation is in meters. Rotation is a normalized
quaternion in `XYZW` order. The transform is `T_T_C`, meaning the controller
output frame expressed in the physical tracker frame. `controller_serial` is
optional; when absent, LTB requires exactly one connected input controller
with the profile's hand role. Exact serial matching is case-sensitive.

Additional schema-1 quality and provenance properties are allowed. Duplicate
known properties, malformed JSON, unsupported schema versions, non-finite
numbers, and materially non-unit quaternions are rejected before runtime
activation.

## Run the one-hand bridge

Run this exact command form from the repository root:

```text
dotnet run --project src/Ltb.App -- bridge --profile <profile.json> --vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--stale-after <seconds>] [--monitor-rate <hz>]
```

A concrete PowerShell example using only example paths is:

```powershell
dotnet run --project src/Ltb.App -- bridge --profile .\left-test-profile.json --vmt-slot 1 --steamvr-settings "C:\LTB-EXAMPLE\steamvr.vrsettings" --stale-after 0.5 --monitor-rate 20
```

`--stale-after` defaults to `0.5` seconds and accepts values from `0.001`
through `300`. It is enforced when a pose adapter reports sample age. The
current synchronous OpenVR source does not expose that age, so LTB still
requires each fresh read to be connected, `RunningOk`, and fully valid, but it
does not claim measured sensor-age freshness. `--monitor-rate` defaults to
`20` Hz and accepts values greater than zero through `1000` Hz. It requests a
minimum watchdog rate: LTB automatically checks faster when necessary to keep
the interval no longer than half `--stale-after` and half the VMT heartbeat
timeout. The effective rate is printed after activation. VMT Alive heartbeat
acquisition and post-activation discovery use a separate five-second bound.

Before any SteamVR device enumeration or tracker/Touch selection gate, the
bridge attempts both selected-slot VMT deactivation and stale exact
source-to-hand mapping release. It refuses to replace another source or hand
owner, and any reported startup-cleanup failure blocks the new configuration.
Only after both cleanup surfaces succeed does it enumerate devices, select the
profile's tracker and Touch controller, and verify their identity and health
along with a fresh VMT heartbeat.

After those gates, LTB sends `/VMT/Set/AutoPoseUpdate(1)`, applies `T_T_C` with
`/VMT/Joint/Driver`, discovers the newly registered VMT path through OpenVR,
and waits for that generic tracker to connect. It requires a fully valid,
`RunningOk` VMT output-pose sample whose pose agrees with
`T_L_tracker * T_T_C` within the runtime safety tolerances. It then rechecks
tracker, Touch, VMT-device, pose-source identity, and heartbeat health
immediately before enabling the exact discovered-path mapping. The same
composed-pose check continues during monitoring. Press Ctrl+C for a normal
SafeDisable.

The composed-pose safety limits are `0.05` seconds maximum tracker/VMT sample
skew, `0.15` meters maximum position error, and `pi/9` radians (20 degrees)
maximum rotation error. They detect a stale or wrong runtime source; they are
not calibration-quality targets. Runtime verification is bounded to five
seconds and continues health monitoring while it waits. Each VMT deactivation
attempt is bounded to two seconds so a stalled transport cannot prevent the
subsequent exact settings-release attempt indefinitely.

This illustrative output uses fake identifiers. The `verification` line is
important: `state: active` means activation and runtime health gates passed;
it does not claim that Touch input and composed-pose provenance were observed
on real hardware.

```text
Lighthouse Touch Bridge - One-Hand Live Bridge
profile: Left hand test mount
hand: left
tracker_serial: LHR-TEST0001
vmt_slot: 1
steamvr_settings: C:\LTB-EXAMPLE\steamvr.vrsettings
state: starting
tracker_device_path: /devices/lighthouse/LHR-TEST0001
controller_serial: CTRL-TEST0001
vmt_device_path: /devices/vmt/VMT_1
verification: The live OpenVR adapter can verify device/pose health, but Touch input provenance and composed-pose provenance require the documented Windows SteamVR hardware check.
effective_monitor_rate_hz: 20
state: active
state: Cancellation
safe_disable_failures: 0
message: Cancellation requested; the one-hand bridge was safely disabled.
```

Before the final three lines are printed on a managed exit, SafeDisable
attempts both operations in order: disable the VMT Joint device, then remove
the exact VMT-source-to-hand mapping. The second operation is attempted even if
the first one fails. The same cleanup path runs on tracker, VMT output-pose,
Touch, VMT-device, or VMT heartbeat failure.

If Ctrl+C cleanup is incomplete, the command does not print that the bridge was
safely disabled. It reports that manual override inspection is required and
returns exit code `4`.

SafeDisable requires the process to remain alive long enough to run it. Ending
the process forcibly, destroying its console, crashing the OS, or losing power
can bypass both cleanup steps. In that case an enabled VMT slot, a persistent
mapping, or both may remain; absence of final output is not evidence of safe
disablement.

## Exit codes

| Code | Meaning |
| ---: | --- |
| `0` | Help, device listing, recording, or bridge cancellation completed without a reported cleanup failure. |
| `1` | Command-line usage or option validation failed. |
| `2` | SteamVR/OpenVR was unavailable, startup or activation failed, or another bounded operational error occurred; no cleanup failure was reported. |
| `3` | A bridge health gate or active runtime health check failed and SafeDisable reported no cleanup failure. |
| `4` | VMT deactivation or exact `TrackingOverrides` release reported at least one cleanup failure. Manual inspection is required. |

## Recovery after abrupt termination

Before reusing a hand after an abrupt termination:

1. keep the headset and controllers non-worn and the tracked area clear;
2. confirm the old LTB process has ended, then inspect the selected VMT slot in
   SteamVR or VMT Manager and inspect the exact source-to-hand entry under
   `TrackingOverrides` in the explicitly selected `steamvr.vrsettings`;
3. retain redacted evidence of any residual device or mapping before changing
   it; do not assume that one surface proves the other is clear;
4. for an intended reuse, run the same bridge command and confirm its
   pre-activation cleanup deactivates the selected slot and releases the stale
   exact mapping before any device-selection gate; if either cleanup reports a
   failure, stop and do not allow the command to activate; and
5. if automatic cleanup cannot be confirmed, stop LTB and SteamVR, preserve the
   current settings file, review an adjacent recognized backup or remove only
   the exact stale mapping, then restart SteamVR and inspect both surfaces
   again before reuse.

Use VMT Manager only after LTB has ended because both claim response port
`39571`. A SteamVR restart clears the prior runtime registration, including a
slot first registered in the wrong device mode, but a persistent settings
mapping must still be inspected and corrected. The controlled Windows test for
this recovery path is in [windows-verification.md](windows-verification.md).

## Settings backups and recovery

LTB touches only the explicit `--steamvr-settings` path. Every operation that
changes the file first creates a unique sibling backup named
`steamvr.vrsettings.ltb-backup`, then `.1`, `.2`, and so on. An idempotent
operation creates no backup. A normal run can create more than one backup
because preparation, activation, and SafeDisable are separate exact-mapping
operations. The backup contains the user's real SteamVR settings and must not
be committed or shared.

Writes use a sibling lock and same-directory flushed staging file, followed by
replacement and post-write validation. If validation fails while LTB can prove
that it still owns the written bytes, it restores the original automatically.
If another process has written later bytes, LTB deliberately does not overwrite
that later writer and reports the recognized backup path.

There is no command-line operation that searches for or automatically selects
a recovery file. For manual recovery:

1. stop LTB and SteamVR so neither can write the selected settings file;
2. use only the exact path originally passed to `--steamvr-settings` and one of
   its adjacent, recognized `.ltb-backup` files;
3. preserve the current settings file separately, compare the candidate backup,
   and confirm that it contains the intended pre-operation state;
4. restore that reviewed backup with normal Windows file tools; and
5. restart SteamVR and confirm the override state in the System Report.

Do not delete all backups, choose one solely by its suffix, search other Steam
installations, or overwrite a file that changed because of another writer.
When code-level recovery is needed, `SteamVrSettingsManager.RecoverFromBackup`
accepts only a recognized sibling backup and creates an undo backup before
replacement.
