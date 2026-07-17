# Setup

## Version 0.1 support boundary

Version 0.1 is a Windows x64 console application. The distributable package is
self-contained, so an end user does not need to install .NET. It does not
install SteamVR, ALVR, VMT, drivers, firmware, or any other third-party
software. There is no GUI, installer, background service, cloud account, or
telemetry.

The production `wizard` command performs live two-hand capture, calibration,
profile persistence, transactional VMT and `TrackingOverrides` application,
and active-use monitoring. The production `bridge` command runs one saved
calibration profile and VMT slot for one hand. The production `daily` command
loads a complete two-hand profile store and applies and monitors both hands.
All three use the live OpenVR, VMT, and SteamVR settings adapters; `wizard` and
`daily` also enforce the active-Lighthouse-HMD dependency gate.

The `wizard-demo` command remains a deterministic fake capture/apply path for
creating and reloading an example two-hand store. It never opens SteamVR, VMT,
or host settings and is not a live calibration command. Automated Linux tests
exercise the same wizard sequencing through injected fakes, including
override release, original-pose capture, persistence, transactional apply,
abort cleanup, and active-HMD rejection. Real OpenVR/VMT/ALVR/SteamVR timing,
device provenance, and hardware behavior still require the unchecked Windows
checks in [windows-verification.md](windows-verification.md).

The exact live and scripted commands are in
[Run the production two-hand wizard](#run-the-production-two-hand-wizard).

## Milestone 5 device combinations

LTB now selects devices through centralized capability inference and a Meta
Touch profile catalog rather than application branches for the initial model
tuple. This broadens the descriptor and configuration contract, but it does
not turn a passing Linux fake into hardware certification. The status terms in
this matrix mean:

- **descriptor-tested** — deterministic fake OpenVR descriptors exercise
  classification and selection on Linux;
- **pipeline-tested** — fake descriptors continue through synthetic
  association/calibration and schema-1 persistence or reuse; and
- **hardware pending** — the corresponding unchecked row in
  [windows-verification.md](windows-verification.md) still requires a real
  Windows, SteamVR, ALVR, and hardware session.

| Role | Device family or combination | Automated status | Windows/hardware status |
| --- | --- | --- | --- |
| Meta input | Quest 2 Touch | Descriptor-tested and pipeline-tested through its recognized Meta Touch family/input profile | Hardware pending |
| Meta input | Quest 3 Touch Plus | Descriptor-tested and pipeline-tested through its recognized Meta Touch family/input profile | Hardware pending |
| Meta input | Quest Pro Touch | Descriptor-tested and pipeline-tested through its recognized Meta Touch family/input profile | Hardware pending |
| Physical pose | Vive Tracker | Descriptor-tested and pipeline-tested through association, synthetic calibration, and schema-1 persist/reload; selected by capability and exact serial, not its model name | Hardware pending |
| Physical pose | Tundra Tracker | Descriptor-tested and pipeline-tested under the same physical-pose capability contract | Hardware pending |
| Physical pose | Generic Lighthouse-tracked device | Descriptor-tested and pipeline-tested when OpenVR reports a connected positional physical pose source; VMT paths are explicitly excluded | Hardware pending for each actual device model |
| Active HMD | Bigscreen Beyond 2/2e | Descriptor-tested with a Beyond 2e fake; the active-display gate accepts positive Lighthouse driver/tracking-system evidence without a model allowlist, but there is no separate Beyond 2 fake | Hardware pending for both models |
| Active HMD | Valve Index | Descriptor-tested through the same active-display Lighthouse-evidence gate | Hardware pending |
| Active HMD | HTC Vive Pro 2 | Descriptor-tested through the same active-display Lighthouse-evidence gate | Hardware pending |
| Active HMD | Other Lighthouse HMD | The gate does not use a display-name/manufacturer allowlist, but it fails closed unless OpenVR index 0 reports positive Lighthouse driver or tracking-system evidence | Hardware pending; treat as unverified until its checklist row passes |

Every intended combination still needs one Meta Touch input controller per
hand, one distinct eligible physical pose source per hand, an active
Lighthouse HMD, ALVR, VMT, and SteamVR. Capability matching does not waive
calibration quality, exact-serial reuse, composed-pose verification,
SafeDisable, or input-provenance checks.

## Windows prerequisites

Install and prepare these components before running the command:

- SteamVR, running with the intended Lighthouse HMD;
- [Virtual Motion Tracker (VMT)](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/setup/),
  installed, enabled in SteamVR add-ons, and visible to the runtime;
- ALVR configured in tracking-reference-only mode for a recognized Meta Touch
  controller profile; `wizard` and `daily` require both left/right roles and
  inputs plus the local dashboard web server on default port `8082`, while
  one-hand `bridge` requires its selected role; and
- each physical Lighthouse pose source named by a profile, powered on, fully
  tracked, position-capable, eligible as a physical source, and identified by
  the exact live serial printed by `devices`.

The portable package includes the .NET runtime. The .NET 8 SDK is required only
when building or running directly from a source checkout with `dotnet run`.

VMT receives commands on loopback UDP port `39570` and sends responses to
`39571`. Close VMT Manager before starting LTB because the manager and LTB use
the same response port. If LTB cannot bind `39571`, it exits with a diagnostic
instead of starting the override. The port assignments and command fields are
defined by the [official VMT API](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/api/).

The production `wizard` and `daily` commands also require a successful,
nonempty response from `http://127.0.0.1:8082/api/version`. If the ALVR
dashboard web-server port
was customized, restore it to the default `8082`, restart ALVR, and confirm that
this URL responds locally before starting LTB. Version 0.1 has no CLI option for
a different ALVR dashboard address or port; configurable-port support is not
present.

If VMT was just installed, enabled, or assigned a device mode for the first
time, complete the VMT-requested SteamVR restart before continuing. From an
extracted package, inspect the live enumeration with:

```powershell
.\Ltb.App.exe devices
```

From a source checkout, the equivalent developer command is:

```powershell
dotnet run --project src/Ltb.App -- devices
```

For a live Windows run, copy each physical pose source's `serial:` value
exactly from this output into `tracker_serial`; matching is case-sensitive. If
`controller_serial` is present, it must likewise exactly match the intended
Touch controller. `LHR-TEST0001`, `CTRL-TEST0001`, and every other identifier
in this document are examples only and must not be used as live hardware
identities. Confirm the physical pose source is a connected generic tracker
and the intended Touch controller has the correct hand role.

For a generalized combination, also confirm the physical source's path is not
a VMT virtual-device path and the Meta Touch controller has a recognized family
and compatible reported input-profile path. SteamVR remains responsible for
choosing the active display. Before `wizard` or `daily` can leave
`DependencyCheck`, LTB inspects OpenVR transient index `0`, requires one
connected `HeadMountedDisplay`, rejects Quest/ALVR/Meta/Oculus evidence, and
requires positive Lighthouse evidence in the driver or tracking-system
metadata. Missing, duplicate, conflicting, or unknown evidence fails closed.
Configure ALVR in tracking-reference-only mode, make the intended Lighthouse
HMD the active SteamVR display, restart SteamVR, and retry.

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

## Install the portable package

1. Obtain `LighthouseTouchBridge-<version>-win-x64.zip` and its adjacent
   `.sha256` file from the approved distribution channel.
2. Verify the ZIP SHA-256 before extracting it. In PowerShell, compare the
   published value with:

   ```powershell
   (Get-FileHash .\LighthouseTouchBridge-<version>-win-x64.zip -Algorithm SHA256).Hash
   ```

3. Extract the ZIP to a user-writable directory. Keep the directory together;
   `Ltb.App.exe`, `openvr_api.dll`, managed assemblies, the self-contained .NET
   runtime, and `licenses/Valve.OpenVR.LICENSE.txt` are one deployment unit.
4. Review `release-manifest.txt`. It records the application version, source
   commit, whether the packaging source tree was dirty, target framework, RID,
   pinned and observed runtime-pack versions, build-tool versions, deployment
   mode, and the OpenVR DLL and Valve-license hashes. Approved release packages
   should report `source_tree_dirty=false` and matching runtime framework and
   pack versions.
5. Start SteamVR and the required integrations, then run
   `.\Ltb.App.exe devices` from the extracted application directory.

Do not copy only the executable, add the package directory to a system-wide
`PATH`, or replace `openvr_api.dll` with an unrelated SDK copy. The portable
ZIP is unsigned in this milestone. Windows signing and SmartScreen evaluation
remain explicit release checks rather than implied package properties.

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

The production `daily` path loads the complete schema-1 store, including
`controller_runtime` and `controller_model`. These are the controller
classification observed when the profile was created and `daily` compares
them with the current recognized Meta Touch classification before reuse. A
different current family requests recalibration; changing stored text does not
make an unsupported live descriptor acceptable. Capability flags and the
OpenVR input-profile path remain current observations rather than persisted
calibration data, so existing valid schema-1 profiles require no migration.

The one-hand `bridge` command deliberately retains its Milestone 2 loader for
the legacy subset shown above: schema version, profile name, hand, optional
controller serial, tracker serial, selected mode, and transform. It ignores
additional schema-1 provenance fields and does not classify or compare
`controller_runtime` or `controller_model`. Its exact serial/role and runtime
health checks are not evidence of controller-family compatibility and must not
be treated as authorization for a generalized combination; use the production
`daily` compatibility gate and the applicable Windows hardware rows for that
claim.

The `daily` command requires one valid store containing exactly one reusable
left profile and one reusable right profile with distinct tracker serials. It
does not calibrate or repair an incomplete store. Use a reviewed store produced
by the production `wizard`; `wizard-demo` creates only a deterministic fake
example for offline testing.

## Run the one-hand bridge

Run this exact command form from the repository root:

```text
dotnet run --project src/Ltb.App -- bridge --profile <profile.json> --vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--stale-after <seconds>] [--monitor-rate <hz>]
```

From an extracted package, omit `dotnet run --project src/Ltb.App --`:

```text
Ltb.App.exe bridge --profile <profile.json> --vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--stale-after <seconds>] [--monitor-rate <hz>]
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

## Run the production two-hand wizard

Use this source-checkout command for first-run capture and calibration:

```text
dotnet run --project src/Ltb.App -- wizard --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--duration <seconds>] [--rate <hz>] [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

From an extracted package, use:

```text
Ltb.App.exe wizard --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--duration <seconds>] [--rate <hz>] [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

The two VMT slots must be distinct. Keep the controllers observable to the
Quest cameras if full 6DoF is desired, then follow the left-only and right-only
multi-axis motion prompts. The first-run production sequence is:

```text
DependencyCheck -> WaitingForSteamVR -> WaitingForDevices -> Ready
 -> OverrideRelease -> Recording(left, right) -> Association
 -> TimeAlignment -> RotationSolve -> TranslationAttempt -> Validation
 -> persist both profiles -> ApplyProfile -> Active
```

`OverrideRelease` first removes and verifies every mapping that references a
selected LTB source or targets either semantic hand. Only after all releases
succeed does it deactivate both selected VMT sources and open the original
Touch pose sources. If any release fails, it attempts the other mapping release
but leaves both sources running and does not start capture. Capture uses the
original Touch streams together with both candidate tracker streams. The existing
association, lag, staged solver, Auto model-selection, quality, and schema-1
persistence components then produce the two profiles. Application uses the
same two-hand VMT and settings transaction as later daily use: both transforms
and mappings must succeed before `Active` is reported.

After activation, the same runtime watchdog used by `daily` monitors Touch,
tracker, VMT, and SteamVR health. Ctrl+C or a health failure enters
`SafeDisable`. For each applied hand, cleanup releases and verifies mappings
that reference the configured or discovered application source or target the
intended semantic hand. It disables that VMT slot only after the settings
operation succeeds. If release fails or times out, the source remains running
so a surviving mapping does not become stale; cleanup continues independently
for the other hand and reports exit code `4`. If capture, persistence,
application, cancellation, or monitoring fails, the command must not claim safe
cleanup while an override is unverified. Unrelated SteamVR settings remain
preserved. A failed first-run capture does not restore a previously active hand
mapping because it could point at a stale source; inspect the settings
explicitly before another attempt if cleanup is reported incomplete.

When the store already contains a complete compatible serial-and-hand pair,
the wizard may take the profile-reuse path and skip capture. Use `daily` for
normal later starts once the stored pair has been reviewed.

`wizard-demo` remains useful for an offline walkthrough:

```text
dotnet run --project src/Ltb.App -- wizard-demo --profiles <profile-store.json> [--log <events.jsonl>]
```

It uses deterministic fake devices, never opens the live adapters, and cannot
establish Windows or hardware readiness.

## Run reliable daily use

Use this source-checkout command after a complete two-hand profile store exists:

```text
dotnet run --project src/Ltb.App -- daily --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

From an extracted package, use:

```text
Ltb.App.exe daily --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

For example, with only example paths and slot numbers:

```powershell
.\Ltb.App.exe daily --profiles .\two-hand-profiles.json --left-vmt-slot 1 --right-vmt-slot 2 --steamvr-settings "C:\LTB-EXAMPLE\steamvr.vrsettings" --log .\events.jsonl --monitor-rate 20 --reconnect-delay 0.25
```

The left and right slots must be distinct and each must be from `0` through
`57`. `--monitor-rate` defaults to `20` Hz and accepts values greater than zero
through `1000` Hz. `--reconnect-delay` defaults to `0.25` seconds and accepts
values greater than zero through `300` seconds. The production daily runtime
uses an internal `0.5`-second pose-staleness threshold, a five-second VMT
heartbeat/discovery bound, and a two-second bound for each independent cleanup
operation.

Before `wizard` captures or `daily` applies either profile, each command
enforces the same production dependency evidence. The local `/api/version`
endpoint above must return a successful, nonempty response. The 500 ms request
is cached for one second, which caps the probe at 1 Hz even when the dependency
or watchdog loop runs faster. OpenVR index `0` must pass the active-Lighthouse-
HMD gate, and the current enumeration must contain exactly one supported
controller per hand. The central device classifier matches a recognized Meta
Touch family from current OpenVR metadata, validates the input-profile path
when OpenVR reports one, and also requires the corresponding left/right role.
Application code does not maintain separate Quest-model string checks.

These are current runtime observations. LTB does not use the stored
`controller_runtime` or `controller_model` fields as evidence that ALVR is
available or that the connected controllers match. It derives the current
runtime/model observation from the recognized live classification only after
the gate passes, then evaluates it against the saved profile. A stored
controller serial, when present, remains an additional exact current-device
constraint.

The watchdog continues both checks after activation. Loss of the local version
proof or a change to the selected controller's current classification or input
profile is treated as Touch input loss and enters SafeDisable; stored profile
text cannot keep either virtual hand active.

`--log` is optional. When supplied, LTB creates the parent directory when
needed and appends one JSON object per event to the selected file. Without it,
the JSONL sink is disabled. Reusing a path appends to the existing file; it does
not truncate a prior run. Keep the file local because event properties can
contain device identities and configuration paths.

## Reliable daily-use lifecycle

The production `daily` command composes the UI-neutral coordinator with the
live Windows adapters and uses this later-run path:

```text
Stopped -> DependencyCheck -> WaitingForSteamVR -> WaitingForDevices
        -> Ready -> ApplyProfile -> Active
```

`Ready` means that dependencies and the stable serial identities required by
the saved profiles are present. It does not mean that an override is already
active. `Active` is emitted only after the complete profile application has
succeeded. A two-hand application is transactional: if either hand fails, the
coordinator rolls back the effects created by that attempt and does not report
`Active`.

Runtime loss never reuses a cached pose while an override remains enabled:

- tracker or Touch loss enters `SafeDisable`, releases and verifies the
  source-centric and semantic-hand mappings for each application, then disables
  the corresponding daily-use VMT profile only after confirmed release, and
  returns to `WaitingForDevices`; a failed mapping release leaves that source
  running and makes cleanup incomplete;
  reacquisition matches the saved stable serial rather than a transient
  OpenVR index;
- VMT loss enters `SafeDisable`, then waits for the VMT dependency and devices
  to become healthy before another apply attempt;
- SteamVR stopping enters `SafeDisable` and then `Stopped`; once this invocation
  has acquired OpenVR, a stop is terminal even if it occurs during VMT recovery,
  so the process does not reopen the session or reapply profiles; and
- a clean application shutdown enters `SafeDisable` and then `Stopped`.

Reacquisition follows the normal `Ready -> ApplyProfile -> Active` gates. It is
not permission to keep or revive a stale virtual-hand pose. If cleanup or
rollback reports a failure, inspect both selected VMT slots and both exact
LTB-owned settings mappings before trying again.

The production composition is present, but Linux transition tests still use
fakes. They do not prove that two real VMT slots, Touch inputs, OpenVR quit
events, or hardware reconnect behave correctly. Complete the Windows checklist
before accepting the live two-hand path.

## Structured diagnostics and logs

Reliable-daily-use state changes and failures are emitted as structured events
with a stable event code, severity, state, message, and UTC timestamp. Optional
context identifies the affected hand or dependency without changing the event
code. Use the code, not only free-form wording, when correlating startup,
reconnect, rollback, and SafeDisable behavior.

Logs and recordings can contain device identities, local paths, motion data,
and runtime configuration details. Keep raw files local, redact stable
identifiers and paths before sharing an excerpt, and never place
`steamvr.vrsettings`, `.ltb-backup` files, or unredacted hardware recordings in
the repository or portable package. Version 0.1 sends no telemetry and has no
cloud log destination.

The `wizard --log <events.jsonl>`, `daily --log <events.jsonl>`, and
`wizard-demo --log <events.jsonl>` options expose `JsonLinesLtbLogSink` as a
local append-only JSON Lines destination. Reusing a path appends another event
sequence; omitting `--log` creates no default event file. Wizard events include
state transitions and the distinct `NoPositionAvailable`,
`PoorTranslationObservability`, and `BadRotationCalibration` results. A logging
failure is not allowed to alter calibration or block SafeDisable or rollback.
The option does not upload, rotate, redact, or delete the selected file.

During active `wizard` or `daily` use, an unexpected adapter exception
produces a
`RuntimeFailure` event before bounded cleanup starts. The event contains the
exception type and message but no stack trace. The coordinator then attempts
all active cleanup surfaces and enters `Stopped`; the cleanup result remains a
separate diagnostic.

SafeDisable requires the process to remain alive long enough to run it. Ending
the process forcibly, destroying its console, crashing the OS, or losing power
can bypass both cleanup steps. In that case an enabled VMT slot, a persistent
mapping, or both may remain; absence of final output is not evidence of safe
disablement.

## Exit codes

| Code | Meaning |
| ---: | --- |
| `0` | Help, device listing, recording, successful `wizard-demo`, or clean `bridge`/`wizard`/`daily` cancellation completed. |
| `1` | Command-line usage or option validation failed. |
| `2` | Startup, capture, calibration, profile loading, profile application, `wizard`, `wizard-demo`, or another bounded operational action failed without a cleanup/rollback failure. |
| `3` | A live runtime health termination, including SteamVR stopping during active use or recovery, completed bounded cleanup without a reported failure; rerun `wizard` or `daily` to start a new session. |
| `4` | SafeDisable or transactional rollback reported at least one failure or timeout. Manual inspection is required. |

## Recovery after abrupt termination

Before reusing a hand after an abrupt termination:

1. keep the headset and controllers non-worn and the tracked area clear;
2. confirm the old LTB process has ended, then inspect the selected VMT slot in
   SteamVR or VMT Manager; under `TrackingOverrides` in the explicitly selected
   `steamvr.vrsettings`, inspect every entry that references a selected
   application source or targets its semantic hand;
3. retain redacted evidence of any residual device or mapping before changing
   it; do not assume that one surface proves the other is clear;
4. for an intended reuse, run the same command and confirm its pre-activation
   cleanup completes before activation; `bridge` deactivates its slot and then
   releases its exact mapping, while `wizard` and `daily` verify removal of
   application-source and semantic-hand mappings before deactivating the
   associated source; if two-hand mapping release fails, confirm the affected
   source remains running and activation stays blocked; and
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

## Build and package from source

Maintainers can produce the supported self-contained publish tree and portable
ZIP on Linux or Windows with a suitable Bash and Python 3 environment:

```bash
dotnet publish src/Ltb.App/Ltb.App.csproj -p:PublishProfile=win-x64
bash build/package-win-x64.sh 0.1.0
```

The profile writes direct publish output under `artifacts/publish/win-x64/`.
The packaging script writes the ZIP and checksum under `artifacts/package/`,
refuses to overwrite an existing same-version archive, verifies the pinned
OpenVR DLL and its license, and stamps the requested version into the assembly
and manifest. Both directories are generated, ignored build output.

The self-contained runtime is intentionally pinned to .NET `8.0.28`. Do not
silently float this value during packaging. Update the publish profile only
after reviewing the .NET servicing release, running the full build and test
suite, confirming the runtime pack recorded in `Ltb.App.deps.json`, and
repeating the Windows package and hardware acceptance checks.

Linux can validate the publish layout, hashes, manifest, ZIP, and checksum. It
cannot launch the Windows apphost, initialize SteamVR, establish hardware
provenance, test Windows ACL behavior, or evaluate code signing and SmartScreen.
Complete those items with [windows-verification.md](windows-verification.md).
