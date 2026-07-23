# Architecture

## Current integration direction: Meta Link and `driver_ltb`

The primary architecture uses Quest through official Meta Quest Link while
Bigscreen Beyond remains SteamVR's sole HMD. Quest and its Touch controllers
stay in the Meta PC runtime and never enter SteamVR. SteamVR and official Meta
Quest Link are the only external runtime dependencies of this path.

```text
Quest + Touch controllers
        |
        | Meta Quest Link
        v
Meta PC runtime -- installed LibOVR, invisible session --> Ltb.MetaLink
                                                               |
                                                               | Touch poses + inputs
                                                               v
Lighthouse trackers ----------------------------------------> Ltb.App
                                                               |
                                                               | calibrate and compose
                                                               | T_output = T_tracker * X_mount
                                                               v
                                              same-user local IPC
                                                               |
                                                               v
                                                        driver_ltb
                                                               |
                                                               v
                                                SteamVR controllers
```

`Ltb.MetaLink` is a C# adapter over the LibOVR runtime installed with Meta
Quest Link. It opens an invisible session, samples Touch poses and full input
state, maps timestamps into the application clock, and exposes project-owned
runtime-neutral contracts. The application associates the Touch and tracker
streams, runs calibration, and composes each final controller pose as
`T_output = T_tracker * X_mount`.

The application sends the final pose and Touch input state over versioned,
same-user local IPC. `driver_ltb` is intentionally thin: it exposes the two
composed controllers to SteamVR, applies fresh state, and marks a controller
untracked when the feed is stale. It does not calibrate, associate devices,
estimate lag, or independently follow a tracker.

The integration is split across four explicit modules:

```text
src/Ltb.MetaLink    installed-LibOVR adapter and Touch-source boundary
src/Ltb.Protocol    versioned IPC schema, codecs, validation, golden vectors
src/Ltb.Driver      C# driver-feed port, transport, readiness, registration
native/driver_ltb   thin native SteamVR driver and portable protocol core
```

Platform-specific code remains behind these narrow boundaries.
`Ltb.Calibration` retains its existing dependency direction and remains free of
UI, SteamVR, OpenVR, Meta Link, LibOVR, driver, and application dependencies.
The detailed protocol, lifecycle, packaging, safety, and verification contract
is documented in [Internal Drivers](internal-drivers.md).

## Existing v0.1 architecture history

The milestone sections below retain the implemented ALVR, VMT, and
`TrackingOverrides` architecture as a buildable fallback only. It receives no
new configuration or orchestration automation, remains runnable only behind
warning-gated `legacy-*` commands until the Meta Link and `driver_ltb` path
passes its documented Windows exit gates, and is then scheduled for removal.

## Milestone 0 boundary

Milestone 0 proves the mount-calibration mathematics from deterministic,
offline pose arrays. It estimates the fixed tracker-to-controller transform
`X_mount` and reports whether the available motion supports rotation-only or
full 6DoF calibration. Live acquisition and runtime-specific adapters do not
participate in this proof.

The coordinate path below shows the transform direction. Every
`T_parent_child` maps coordinates from the child frame into the parent frame,
so the rightmost transform is applied first.

```text
C -- X_mount = T_T_C --> T -- T_L_T(t) --> L -- Y = T_Q_L --> Q
controller               tracker              Lighthouse       Quest world

T_Q_C(t) = Y * T_L_T(t) * X_mount
```

`Q`, `L`, `T`, and `C` are right-handed Cartesian frames. Rotations use
normalized `System.Numerics.Quaternion` values with components reported in
`XYZW` order. Translation is measured in meters. Sample timestamps are
monotonic host time in seconds, not wall-clock time and not comparable across
host boots.

At runtime the Quest-world transform is absent:

```text
T_L_output(t) = T_L_tracker(t) * X_mount
```

This order rotates the mount translation by the current tracker orientation
before adding the tracker position. It therefore preserves the physical
lever arm in a full 6DoF result; a rotation-only result fixes that translation
to zero.

## Dependency direction

The implemented offline path has the following dependency graph:

```text
Ltb.Core
   ^
   |
Ltb.Calibration <---- Ltb.SyntheticData
   ^                       |
   +--- Ltb.Calibration.Tests
```

- `Ltb.Core` owns rigid transforms, frame and unit conventions, validity
  flags, timestamped pose samples, and synchronized pose pairs. It has no
  project dependency.
- `Ltb.Calibration` depends only on `Ltb.Core`. It contains the deterministic
  hand-eye solver, observability and quality gates, result models, and a
  human-readable report. It has no UI, SteamVR, OpenVR, ALVR, VMT, or
  application dependency.
- `Ltb.SyntheticData` depends on `Ltb.Core` and `Ltb.Calibration`. It generates
  seeded fixtures, invokes the solver, and prints the Milestone 0 console
  report.
- `Ltb.Calibration.Tests` exercises these public contracts and the synthetic
  path. Test-framework packages do not enter the production dependency graph.

Production math uses only .NET and `System.Numerics`; the small symmetric
eigenproblems and translation least-squares solve are implemented inside the
calibration library. Platform projects remain integration boundaries rather
than dependencies of the calibration domain.

## Milestone 1 acquisition and replay boundary

Milestone 1 adds an acquisition path around the existing offline calibration
core. The platform boundary discovers devices and samples poses, while the
portable domain records, serializes, aligns, inspects, and replays those
samples. A saved recording therefore follows the same solver path as a live
capture after acquisition:

```text
SteamVR/OpenVR runtime (Windows)
              |
              v
   Ltb.OpenVr native adapter
              |
              v
Ltb.Core recording contract ------> versioned recording file
              |                              |
              +------------------------------+
              v
Ltb.Calibration time alignment and offline solver
              |
              +------> Ltb.RecordingInspector summary
              +------> calibration result and report
```

`Ltb.OpenVr` is the only project allowed to translate OpenVR device classes,
device indexes, properties, tracking results, and pose structures. Its public
boundary uses project-owned device descriptors and timestamped pose samples;
OpenVR types do not cross into `Ltb.Core`, `Ltb.Calibration`, tools, or the
application coordinator. The generic source roles are named
`TrackedPoseSource` and `InputControllerPoseSource` so the portable consumers
do not depend on a specific tracker or controller vendor.

The native adapter uses Valve's official generated C# binding and Windows x64
native library, both pinned to upstream OpenVR SDK 2.15.6 commit
`0924064316de3effbcd1acf1e309182a2deb1c05`. The binding comes from
`headers/openvr_api.cs` (SHA-256
`c17e878b7b3b925d1f22ef5382561389c47db8b92019de840705ff5ff28c317a`)
and the native asset comes from `bin/win64/openvr_api.dll` (SHA-256
`bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a`).
Precise upstream URLs, Git blob IDs, and hashes are retained in the source-tree
file `src/Ltb.OpenVr/ThirdParty/OpenVR/README.md`.

The binding is compiled from source into the private `Ltb.OpenVr.Interop`
assembly rather than restored from a community NuGet package. Its Valve types
are consumed only inside the platform project and are not returned by public
LTB contracts. The official 837,272-byte PE32+ x86-64 library is stored at
`src/Ltb.OpenVr/runtimes/win-x64/native/openvr_api.dll`. For RID-neutral and
`win-x64` builds, `Ltb.OpenVr.csproj` publishes it as `openvr_api.dll` in the
consumer application root, beside `Ltb.App.exe` and the interop assembly. The
generated binding's `DllImport("openvr_api")` therefore uses the application
deployment instead of requiring a user-managed `PATH`. Explicit builds for
other RIDs do not receive the Windows x64 asset.

Valve's 3-clause BSD notice is retained at
`src/Ltb.OpenVr/ThirdParty/OpenVR/LICENSE` and copied into application outputs
as `licenses/Valve.OpenVR.LICENSE.txt`. A deterministic integration test checks
the deployed native binary and notice hashes without loading the DLL. The
concrete backend still cannot be initialized on Linux: a real Windows x64
SteamVR runtime is required. Deterministic simulated sources exercise
enumeration, capture, serialization, replay, and lag estimation without native
calls, while live initialization, enumeration, and sampling remain deferred to
the [Windows verification checklist](windows-verification.md).

`Ltb.App` owns console command wiring and production composition only.
Milestones 1-2 were completed before the desktop project was introduced.
Avalonia 11 is now the selected desktop framework, with its dependency isolated
in `Ltb.Gui`; the decision and current composition boundary are recorded below.

## Recording and replay contract

Recordings are UTF-8 JSON with format identifier `ltb-pose-recording` and
`schemaVersion` 1. `PoseRecording` contains uniquely named
`PoseStreamRecording` values. Each stream has a `PoseStreamIdentity` containing
its stream ID, runtime-neutral `inputController` or `trackedPose` source kind,
stable device ID, and optional display name. Samples within each stream have
strictly increasing host timestamps. Each `RecordedPoseSample` preserves:

- a monotonic host timestamp in seconds, taken when the sample enters LTB;
- the source pose and independent orientation, position, tracking-valid, and
  connectivity state;
- the runtime-neutral tracking result;
- optional runtime pose time, prediction offset, and sample age, all in
  seconds; and
- orientation as a normalized quaternion in `XYZW` order and translation in
  meters.

The host timestamp is the canonical time coordinate for ordering and stream
alignment. It is local to one recording and must not be interpreted as wall
clock time or compared across host boots. Runtime timing fields are retained
as observations; they do not silently replace the host timestamp or alter the
recorded transform. Validity and connectivity are data, not reasons to delete
a sample from the file.

`PoseRecordingJson` writes culture-independent JSON in fixed property and
stream/sample order. It rejects a different format identifier, unsupported
schema version, missing or mistyped required properties, non-finite numbers,
invalid transforms, duplicate stream IDs, and non-increasing host timestamps.
The device ID may contain a runtime serial, so recordings and unredacted
inspector output are local data rather than repository fixtures.

Replay does not consult the wall clock, sleep to reproduce capture timing, or
enumerate live devices. Given the same recording and calibration options,
replay performs the same interpolation, sample selection, lag search, and
solver operations in the same order and produces the same result. Synthetic
generation uses an explicit seed so a fixture can be regenerated rather than
depending on a machine-local capture.

`StreamLagEstimator` computes quaternion-sign-continuous angular-speed
magnitudes and searches a bounded offset by normalized cross-correlation. A
positive `LagEstimate.LagSeconds` means that the controller host timestamps
occur later than tracker timestamps for the same motion; this is the negative
of `tau` in specification section 10. `PoseStreamAligner` corrects controller
time, uses shortest-path SLERP for orientation and linear interpolation for
position, and constructs `SynchronizedPosePair` values. Translation is
attempted only after timing and rotation have passed their quality checks.

## Data contract

`TimestampedPoseSample` combines a finite rigid transform, a non-negative
monotonic timestamp, and independent orientation, position, and tracking-valid
flags. A channel is usable only when its channel flag and tracking-valid flag
are both present. `SynchronizedPosePair` accepts tracker and controller
timestamps within one microsecond and exposes their midpoint as the aligned
time. The solver additionally requires those aligned times to increase
strictly and permits a caller-configured timestamp limit no larger than the
pair-construction tolerance.

`RecordingCalibrationReplay` applies `StreamLagEstimator` and
`PoseStreamAligner` before this solver API boundary, then invokes
`HandEyeCalibrationSolver` with the resulting pairs. `RecordingReplayOptions`
selects the two stream IDs and fixes lag, alignment, and calibration options,
so replay has no dependency on device enumeration or live time.

## Milestone 2 one-hand live bridge boundary

Milestone 2 applies one saved profile to one hand. It composes the portable
transform contract with two platform adapters: VMT receives the tracker-local
mount transform, and SteamVR receives one `TrackingOverrides` mapping from the
discovered VMT device path to the selected semantic hand. This implements
specification sections [15, 16, 18, and 24](specification.md) without moving
VMT, OpenVR, SteamVR settings, or application policy into `Ltb.Core` or
`Ltb.Calibration`.

The activation and cleanup order is:

```text
profile T_T_C + requested hand + bounded VMT slot
                 |
                 v
start VMT response pump
                 |
                 v
pre-activation cleanup: deactivate selected slot + release stale exact mapping
                 |
                 v
if either cleanup fails, report both attempts and do not activate
                 |
                 v
OpenVR discovery: physical tracker and Touch controller
                 |
                 v
healthy tracker read -> fresh VMT heartbeat -> healthy tracker re-read
                 |                         +-> Touch still connected
                 v
/VMT/Set/AutoPoseUpdate(1)
                 |
                 v
/VMT/Joint/Driver(enable, tracker serial, T_T_C)
                 |
                 v
discover the newly registered VMT path; wait for its GenericTracker to connect
                 |
                 v
valid VMT output pose matching tracker * T_T_C within safety tolerance
                 |
                 v
final tracker/Touch/device/heartbeat identity and health gate
                 |
                 v
enable exact discovered-path override
                 |
                 v
verify one-hand device/pose contract -> monitor all health sources
                 |
   cancel, fault, tracker/Touch/VMT loss
                 v
SafeDisable: attempt VMT deactivate, then always attempt exact mapping release
```

`Ltb.App` is the coordinator and console boundary. `Ltb.Vmt` and `Ltb.OpenVr`
each depend on `Ltb.Core`, but not on each other; the application composes their
narrow contracts. `Ltb.Calibration` remains independent of all three projects.

### Transform and VMT command decision

The internal mount transform is `T_T_C`: it maps controller-frame coordinates
into the physical tracker frame. Frames are right-handed, translation is in
meters, and the normalized quaternion is stored and serialized in `XYZW`
component order. Runtime composition remains:

```text
T_L_output(t) = T_L_tracker(t) * T_T_C
```

The VMT adapter deliberately uses `/VMT/Joint/Driver`, not a `Follow` command.
The [official VMT API](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/api/)
defines `Joint/Driver` in OpenVR's right-handed driver coordinates with both
position and rotation relative to the target device. That is the command whose
geometry matches `T_T_C`; `Follow` keeps a room-space component and cannot
represent the complete rigid tracker-local transform. VMT auto pose update is
enabled before the slot is activated so the serial-following Joint pose is
recomputed continuously. Deactivation disables only this slot and leaves the
global auto-update setting unchanged because another VMT client may use it.

The enabled Joint command always requests VMT device mode `1` (`Tracker`). VMT
chooses a slot's OpenVR device type when that slot first registers during the
current SteamVR process; a later command does not reclassify an already
registered slot. The coordinator therefore requires the discovered slot to be
a `GenericTracker`. If another tool first registered that slot in a different
mode, the operator must stop LTB, restart SteamVR, and retry so the slot can
first register in Tracker mode.

`VmtTransformConvention` is the single conversion layer between internal and
wire conventions. The current conversion is intentionally the identity:
right-handed axes, meters, `T_T_C`, and `XYZW` are unchanged. Integration tests
round-trip this adapter and verify that composition is tracker pose times mount
transform. A future VMT convention change belongs in this adapter rather than
in calibration or profile code.

### Discovered device paths and settings ownership

The OpenVR adapter reads `Prop_RegisteredDeviceType_String` and normalizes a
valid `<driver>/<device>` value to `/devices/<driver>/<device>`. It reads the
remaining device metadata independently, so an unavailable registered-device
type does not discard observed manufacturer, model, controller, input-profile,
version, or tracking-system evidence. `Prop_TrackingSystemName_String` and
`Prop_ActualTrackingSystemName_String` remain separate observations: the latter
is not collapsed into, or discarded in favor of, the former. Active-HMD policy
uses both for positive Lighthouse evidence and runtime-exclusion vetoes. The
driver ID remains unavailable when it cannot be parsed from a canonical
registered-device path and is never inferred from those other properties. A
fresh VMT
slot might not exist in OpenVR until its first enabled Joint command, so the
coordinator does not require a descriptor before activation. It uses the
bounded VMT slot's canonical path only to remove a stale exact LTB mapping,
then activates the slot, enumerates OpenVR, and requires exactly one connected
generic tracker whose registered device path parses back to that slot. The
actual enumerated path becomes the override-activation and cleanup binding; no
transient OpenVR device index becomes a settings key. Valve documents
registered device types and generic tracker classes in the
[OpenVR driver API](https://github.com/ValveSoftware/openvr/blob/master/docs/Driver_API_Documentation.md).

The physical tracker may not itself resolve to the requested VMT slot; that
self-follow configuration is rejected before activation. Pose-source bindings
and every re-enumerated tracker, Touch, and VMT descriptor must retain the
expected stable ID, device path, transient index, class, and controller role.
Disappearance, substitution, or transient-index reuse is a health failure even
when another descriptor has a similar class.

`SteamVrSettingsManager` operates on the one `steamvr.vrsettings` path supplied
by the caller and never searches the host. For each non-idempotent change it:

1. takes an exclusive sibling `.ltb-lock` with a bounded wait;
2. reads and parses the JSON object, preserving unrelated sections and values;
3. rejects an existing source or hand owner instead of replacing it;
4. writes the original bytes to a unique sibling `.ltb-backup` file;
5. flushes a same-directory staging file and replaces the target by rename;
6. parses and compares the written JSON with the intended merge, then validates
   the exact mapping state; and
7. restores the original bytes automatically if validation fails and LTB can
   prove that no later writer has replaced its result.

The sibling lock serializes cooperating LTB processes. SteamVR or another
external writer does not honor it, so pre-write byte comparisons detect known
changes and the post-write ownership check refuses to overwrite a later
winner. Manual recovery accepts only a recognized backup adjacent to the
explicit settings file, and backs up the content it replaces. Releasing an
override removes only the exact source-to-hand mapping and deliberately
preserves `activateMultipleDrivers`, unrelated mappings, and unrelated
settings. The [VMT Tracking Override documentation](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/api/#tracking-override)
also records the expected source-to-semantic-hand direction and that disabling
the virtual tracker releases the runtime override.

### Health, verification, and scope

Tracker health, VMT output-pose health, and VMT driver health are independent.
Both the physical tracker and discovered VMT output must remain connected,
`RunningOk`, orientation-valid, position-valid, and tracking-valid. When an
adapter supplies sample age, `--stale-after` rejects an age at or above the
threshold. The synchronous OpenVR pose API used here does not expose sensor
age, so an unavailable age cannot be treated as measured freshness; a fresh
synchronous read plus the runtime validity flags is the available evidence.
VMT driver liveness instead comes from `/VMT/Out/Alive`; initial heartbeat
acquisition and post-activation discovery each use a separate five-second
bound.

`--monitor-rate` requests a minimum watchdog rate. The coordinator shortens
the interval when necessary so it is no longer than half `--stale-after` and
half the VMT heartbeat timeout. The effective rate is printed on activation.
This prevents a low requested rate from making the health loop itself slower
than the freshness limits it enforces.

The legacy one-hand `bridge` keeps its original VMT-first managed-exit order:
it sends the disabled Joint configuration, then attempts exact settings release
even if VMT deactivation failed. Cleanup failures are returned and produce a
distinct command exit code rather than being hidden. This order is specific to
the one-hand coordinator; the production two-hand `wizard` and `daily` paths
use the source-preserving order described below. Touch
disconnect, VMT device loss or identity change, stale VMT heartbeat, invalid or
stale VMT output pose, invalid tracker pose, reported tracker staleness,
cancellation, and handled activation failures all enter this cleanup path once
preparation has succeeded and the process remains able to execute cleanup.

That guarantee cannot cover forced process termination, destruction of the
owning console, an OS crash, or power loss. Those events can stop cleanup code
entirely and may leave both an enabled VMT slot and its persistent
`TrackingOverrides` mapping. Before reuse, the operator must keep the setup
non-worn, inspect the VMT device and exact settings mapping, and either allow
the next run's pre-activation cleanup to clear both surfaces or perform the
reviewed manual recovery in [setup.md](setup.md). A SteamVR restart unloads
runtime device state but does not replace inspection and correction of a
persistent settings mapping.

Startup cleanup has the same two-surface rule before any SteamVR device
enumeration or selection gate and before any new activation. The requested
slot and profile hand are sufficient to attempt VMT deactivation and stale
exact mapping removal; either cleanup is attempted even if the other fails.
Any reported startup cleanup failure blocks enumeration-dependent activation.
This lets a recovery run clear stale state even when the physical tracker or
Touch controller is not yet discoverable, and prevents an old live slot from
surviving merely because its persistent JSON entry was removed.

After VMT discovery, the coordinator compares the actual VMT OpenVR pose with
`T_L_tracker * T_T_C` before enabling the override and on every health cycle.
The Milestone 2 safety bounds are at most `0.05` seconds sample-time skew,
`0.15` meters position error, and `pi/9` radians (20 degrees) rotation error,
in addition to each source's validity flags. These are fail-safe mismatch
limits, not calibration-quality acceptance targets. A valid-but-wrong Joint
transform therefore enters SafeDisable rather than remaining a pose source.

Linux integration tests use fake OpenVR devices, fake pose sources, fake VMT
transports with loopback-source filtering, fixture settings files, and fake
verification probes. They prove OSC encoding, transform conversion, safe merge
and recovery, activation order, verification-contract enforcement, monitoring,
and SafeDisable. The end-to-end fake verifier produces its observed input
identity, pose-source path, and composed pose independently instead of echoing
the coordinator's expected request. These tests do not prove that a real
SteamVR session retains Touch inputs while accepting the VMT pose. That
provenance and restart behavior remain explicit acceptance items in
[windows-verification.md](windows-verification.md).

Milestone 2 was delivered without a GUI dependency. The later Avalonia shell
retains the same platform boundaries, as does the Milestone 4 daily-use
coordinator described below.

## Application boundary and desktop UI decision

`Ltb.App` remains a console composition and command-wiring boundary, and
Milestones 1-3 introduced no UI dependency there. Avalonia 11 is the selected
desktop UI framework; that decision was recorded here before the dependency was
introduced in the separate `Ltb.Gui` project. The owner directed the choice to
minimize OS dependency: Avalonia is
cross-platform, so the GUI is developed and headless-tested on Linux and
cross-published to win-x64, and specification section 22 lists it among the
candidate frameworks.

The GUI lives in a separate `Ltb.Gui` project as a thin view over the existing
UI-neutral ports: the `TwoHandCalibrationWizard` state machine, its
`ICalibrationWizardOutput` events, and `ILtbLogSink` structured logs. View
code contains rendering and binding only; sequencing, device, calibration, and
persistence policy stay in the existing wizard, runtime, and backend types.
`Ltb.Calibration` and `Ltb.Configuration` remain free of UI dependencies.

The desktop shell defaults to the deterministic scripted-demo mode, so it can
be opened without SteamVR, OpenVR, VMT, or host-settings access. The user can
select production mode with the in-window radio buttons or start the executable
with the `wizard` verb; `wizard-demo` explicitly selects the scripted path.
The same launch values remain editable in the window. Both modes expose
`--profiles` and optional `--log`; production additionally exposes
`--left-vmt-slot`, `--right-vmt-slot`, `--steamvr-settings`, `--duration`,
`--rate`, `--monitor-rate`, and `--reconnect-delay`. Configuration is locked
while a session runs, and Abort or a window close requests cancellation and
waits for the session's cleanup path.

`CalibrationWizardViewModel` parses the editable values using invariant numeric
formats and rejects invalid configuration before creating a session. The public
`ProductionCalibrationWizardSessionOptions` contract in `Ltb.App` performs the
authoritative range and cross-field validation, including distinct VMT slots in
the supported `0..57` range. `ProductionCalibrationWizardSessionFactory` is the
shared composition seam used by both the console `wizard` command and the GUI:
it owns the live runtime, file-backed profile store, UI-neutral wizard,
post-activation watchdog, structured log, SafeDisable behavior, and native
resource lifetime. `Ltb.Gui` adapts the completed lifecycle result to
`ICalibrationWizardSession`; neither its view nor its view model sequences
devices, solves calibration, applies profiles, or defines cleanup policy.

```text
Ltb.Gui arguments + editable fields
                 |
                 v
      CalibrationWizardViewModel
       | scripted          | production
       v                   v
ScriptedCalibration  ProductionCalibrationWizardSessionFactory (Ltb.App)
WizardSession              |
                           v
             production runtime + profile store
                           |
                           v
              TwoHandCalibrationWizard
                           |
                           v
              watchdog -> SafeDisable
```

Production validation and native-runtime failures are reported through bounded
wizard diagnostics rather than escaping into view callbacks. Linux GUI tests
select both modes, validate every production parameter boundary, exercise the
shared production composition through injected fake backends, and use the
headless Avalonia test host. They do not open live runtime resources or replace
the Windows launch, visual, SteamVR, ALVR, VMT, and hardware checks.

## Milestone 3 two-hand wizard boundary

Milestone 3 adds a UI-neutral `TwoHandCalibrationWizard` state machine in
`Ltb.App`. It emits state, capture-progress, diagnostic, quality, and profile
events through `ICalibrationWizardOutput`; the console renderer is one consumer
of those events. The Avalonia UI implements the same output and runtime ports
without moving device, calibration, or persistence policy into view callbacks.

```text
ScriptedCalibrationWizardRuntime     production OpenVR/VMT/settings adapters
                         \           /
 ICalibrationWizardRuntime (dependencies, devices, capture, apply)
                         |
                         v
              TwoHandCalibrationWizard
                 |                 |
                 v                 v
 ICalibrationWizardBackend   ICalibrationWizardOutput
       |           |           console / Avalonia GUI
       v           v
Ltb.Calibration  Ltb.Configuration
```

The application owns sequencing only. `TrackerHandAssociator` owns
coordinate-invariant angular-motion correlation and ambiguity rejection;
`MotionCoverageAnalyzer` owns tracking-validity and excitation coverage;
`PerHandCalibrationPipeline` owns the existing lag, alignment, staged solver,
Auto selection, fallback, and quality gates. `Ltb.Configuration` owns schema-1
validation, JSON, exact serial-and-hand matching, and atomic file persistence.
No association, lag, transform solve, quality threshold, or JSON rule is
duplicated in the application layer.

The first-run flow is:

```text
DependencyCheck -> WaitingForSteamVR -> WaitingForDevices -> Ready
 -> OverrideRelease -> Recording(left, right) -> Association
 -> TimeAlignment -> RotationSolve -> TranslationAttempt -> Validation
 -> persist both profiles -> ApplyProfile -> Active
```

Each guided capture contains one original Touch stream and both candidate
tracker streams. The two captures ask the user to move one hand at a time, so
serial assignment is based on motion correlation and remains correct when
tracker enumeration order is reversed. A capture runtime can emit repeated
prefix snapshots; every snapshot obtains total rotation, axis coverage,
validity, progress, and separate rotation/position readiness directly from
`MotionCoverageAnalyzer`. Per-hand pipeline results are then
reported through the named analysis states. A rejected rotation returns to
`Ready` with a retry diagnostic. Missing controller position or poor
translation observability is instead a successful Auto result with a stored
zero translation and an explicit `rotation_only_fallback` reason.

On later runs, the backend loads the profile store, matches one schema-1
profile for each detected tracker serial and semantic hand, and takes the
short path `Ready -> ApplyProfile -> Active`. Capture and calibration are
skipped only when the pair is complete and `RecalibrationEvaluator` accepts the
runtime observations: explicit request, observed hand association, mount-moved
flag, validation threshold, controller runtime/model, expected schema, and
transform convention. A recognized stored-schema mismatch takes the capture
path and atomically replaces the incompatible store after both new profiles
validate; malformed JSON remains a fail-safe diagnostic. Apply remains behind
the runtime port.

Two runtime compositions now implement that port. `wizard-demo` uses
`ScriptedCalibrationWizardRuntime` and deterministic fake streams without
opening SteamVR, OpenVR, VMT, or host settings. The production `wizard` opens
the live OpenVR session, uses the Milestone 1 recorder for the original Touch
and tracker streams, and reuses the same VMT, SteamVR settings, two-hand apply
transaction, watchdog, and SafeDisable boundaries as `daily`.

The production composition is:

```text
wizard CLI -> TwoHandCalibrationWizard -> live OpenVR recorder
                                  |     -> CalibrationWizardBackend
                                  |     -> schema-1 profile store
                                  v
                         two-hand apply transaction
                                  |
                                  v
                     Active -> watchdog -> SafeDisable
```

Before original Touch capture, the runtime performs a two-phase safety pass.
It first releases and verifies, for both hands, every mapping that references an
LTB application source or targets the intended semantic hand. Only after every
release succeeds does it deactivate the selected VMT sources. If any release
fails or times out, no selected source is deactivated and recording does not
start; other mapping-release attempts still run, unrelated settings remain
unchanged, and cleanup is reported incomplete. The recorder therefore cannot
silently sample an already overridden Touch pose or create a stale surviving
override during preparation.

Both calibrated profiles persist before the apply transaction starts. `Active`
is allowed only after both VMT transforms and source-to-hand mappings apply; if
either side fails, the transaction rolls back effects from that attempt.
Cancellation or failure before or during capture also runs cleanup. Successful
cleanup leaves no active hand override and preserves unrelated SteamVR
settings; it does not reactivate a prior hand mapping that could name a stale
source.

The production command is:

```text
dotnet run --project src/Ltb.App -- wizard --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--duration <seconds>] [--rate <hz>] [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

The deterministic command remains:

```bash
dotnet run --project src/Ltb.App -- wizard-demo --profiles <profile-store.json> [--log <events.jsonl>]
```

It uses deterministic fake controllers and fake tracker serials, intentionally
reverses tracker enumeration, selects full 6DoF for the left hand and normal
rotation-only fallback for the position-unavailable right hand, writes two
profiles, and reloads them on the next invocation without native runtime calls.
It is not a live hardware command. Automated fake-backed tests prove the
production composition boundary, including release-before-capture, apply
rollback, abort cleanup, and active-HMD rejection, but real runtime timing and
device provenance remain Windows checks.

## Milestone 4 reliable daily-use boundary

Milestone 4 adds a UI-neutral `ReliableDailyUseCoordinator` in `Ltb.App`. It
owns sequencing and recovery policy, while health observations, states, and
structured event contracts remain runtime-neutral. OpenVR, VMT, profile
storage, and settings operations stay behind narrow interfaces so the complete
transition matrix can run with deterministic fakes on Linux.

The production composition is:

```text
daily CLI -> FileCalibrationWizardBackend + JsonLinesLtbLogSink
          -> ReliableDailyUseCoordinator
          -> ProductionReliableDailyUseRuntime
          -> shared OpenVR session + one VMT client + SteamVrSettingsManager
```

One shared OpenVR session and one VMT response pump serve two distinct VMT
slots. The CLI requires the complete profile store, left and right slots in
`0..57`, and an explicit settings path:

```text
daily --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

The monitor rate defaults to `20` Hz and reconnect delay to `0.25` seconds.
The live adapter uses an internal `0.5`-second pose-staleness threshold and a
five-second VMT heartbeat/discovery bound.

The `daily` composition proves input and active-HMD readiness with three
independent current observations before it can become `Ready`:

1. `AlvrLocalDashboardProbe` requires a successful, nonempty response from the
   loopback endpoint `http://127.0.0.1:8082/api/version`. Version 0.1 fixes this
   address and port; it has no configurable dashboard-port CLI option. The HTTP
   request has a 500 ms bound, and the last result is reused for one second so
   dependency and watchdog loops cannot probe more often than 1 Hz.
2. `ActiveHmdReadiness` requires exactly one connected
   `HeadMountedDisplay` at transient OpenVR index `0`, rejects
   Quest/ALVR/Meta/Oculus evidence, and requires positive Lighthouse evidence
   in the current driver or tracking-system metadata. Missing, conflicting, or
   unknown evidence fails closed with the tracking-reference-only and intended-
   Lighthouse-HMD remediation.
3. Current OpenVR properties must expose exactly one supported controller for
   each hand. A central profile catalog recognizes the Quest 2 Touch, Quest 3
   Touch Plus, and Quest Pro Touch families from current driver, tracking
   system, manufacturer, role, model, controller type, and optional input-
   profile properties after normalization.

The local version endpoint proves that the ALVR process is currently serving
its dashboard API; the OpenVR classification proves that the current controller
devices match one supported Meta Touch family. Neither proof is taken from a
stored calibration profile. The runtime derives current recalibration
observations such as `ALVR` plus `Quest 3 Touch Plus` only after both live hand
descriptors classify to the same runtime/model, then compares them with stored
profile values. A stored runtime/model claim cannot make an absent or
mismatched current device acceptable.

The ALVR endpoint and controller-descriptor observations remain watchdog
conditions after activation. Loss of the
local ALVR proof or a change to the selected controller's current OpenVR
identity or metadata becomes `TouchInputLost` and triggers SafeDisable; the
runtime never substitutes a stored tuple for the missing live observation.

Readiness diagnostics preserve the failed boundary. An unavailable ALVR
endpoint or failed active-HMD gate reports `DependencyUnavailable`. During
device readiness, a missing or unsupported Meta Touch descriptor or physical
pose source reports
`DevicesUnavailable`, while a missing or stale VMT Alive heartbeat reports
`VmtUnavailable`. A VMT
recovery dependency loop can first emit `DependencyUnavailable` with its
VMT-specific diagnostic, but it is never presented as a controller or device-
classification failure.

The normal later-run flow is:

```text
                    dependency available
Stopped -> DependencyCheck -> WaitingForSteamVR
                                  |
                                  v
                         WaitingForDevices
                                  |
                                  v
Ready -> ApplyProfile --success--> Active
  ^            |
  |            +--failure/partial apply--> rollback --> Ready or Stopped
  |
  +-------- healthy stable-serial reacquisition --------+
```

`Ready` records that the required dependencies and stable device identities
are healthy; it does not imply an active override. `ApplyProfile` is the only
entry to `Active`. Transient OpenVR indexes are observations, not identity
keys, so reconnect always re-enumerates and matches the stored stable serial,
semantic hand, device class, and role.

### Watchdog and reconnect transitions

The active watchdog treats pose health, Touch input health, VMT health, and
SteamVR availability as separate observations. The coordinator does not hold a
last-known pose through a failure:

```text
Active + tracker lost  -> SafeDisable -> WaitingForDevices
Active + Touch lost    -> SafeDisable -> WaitingForDevices
Active + VMT lost      -> SafeDisable -> dependency/device wait
Active + SteamVR stop  -> SafeDisable -> Stopped
clean shutdown         -> SafeDisable -> Stopped
```

Two-hand SafeDisable is complete only after every relevant source/semantic-hand
mapping is released and each corresponding virtual source is disabled. Cleanup
is source-preserving and ordered per application: atomically release or roll
back every mapping that references the configured or discovered application
source or targets its intended semantic hand, verify that bounded settings
operation, and only then deactivate that VMT source. Unrelated mappings and
settings are preserved. If mapping cleanup fails or times out, the source
remains running so any surviving override still has a live pose source. The
coordinator records the failure, skips deactivation for that source, and
continues independent cleanup for the other hand. A later VMT-deactivation
failure is also reported. Any failure makes cleanup incomplete and requires
exit code `4` plus manual inspection.
Reacquisition must then pass the normal `Ready -> ApplyProfile -> Active` path;
it cannot reactivate a cached mapping or stale source.

OpenVR quit events are health inputs rather than unhandled process events. A
runtime quit is acknowledged through OpenVR and classified as stopped; a
driver-requested quit is also classified as stopped. A process-quit event for
another client is ignored. A terminal runtime event therefore drives the
normal `SteamVrStopped` diagnostic and SafeDisable path.

SteamVR startup retry is allowed only before this `daily` invocation has
acquired its OpenVR session. If SteamVR stops after acquisition, including
while recovering from VMT loss, the stop is terminal for that invocation. The
coordinator performs bounded cleanup, emits `SteamVrStopped`, enters `Stopped`,
and does not reopen OpenVR or reapply profiles. With successful cleanup the CLI
returns exit code `3`; cleanup or rollback failure retains exit code `4`.

This policy prevents a device-loss interval from leaving a permanently frozen
virtual hand. The guarantee applies to managed execution. Forced process
termination, console destruction, OS crash, or power loss can prevent the
cleanup code from running. A later start therefore attempts cleanup before any
new apply, and the operator retains the documented manual recovery path.

### Transaction and rollback ownership

Profile application is one logical transaction across both hands. The apply
adapter returns enough rollback information to reverse only effects introduced
by the current attempt. If the second hand fails after the first succeeds, the
coordinator rolls back the first, reports the apply failure and any rollback
failure separately, and never emits `Active` for the incomplete pair.

This application transaction complements rather than replaces the existing
settings-file transaction. `SteamVrSettingsManager` continues to own sibling
locking, unique byte-exact backups, same-directory staging, atomic replacement,
post-write validation, ownership-aware restoration, and reviewed recovery.
Profile persistence likewise remains owned by `Ltb.Configuration`. The
coordinator composes these results but does not duplicate their JSON, locking,
or file-recovery rules.

Rollback is not allowed to overwrite an external winner. If another writer
changes settings after LTB's write, or a rollback operation itself fails, the
coordinator enters a non-active state with a diagnostic and requires manual
inspection. A process crash can still interrupt an in-memory multi-hand
transaction, which is why startup cleanup and the manual recovery procedure
remain part of the architecture.

### Structured event model

State changes, dependency observations, health failures, SafeDisable attempts,
reconnect decisions, apply results, and rollback results are represented as
structured log events. Each event has a stable code, severity, state, message,
and UTC timestamp, with optional hand or dependency context. Tests assert codes
and transitions instead of parsing console prose. Platform adapters report
facts; `ReliableDailyUseCoordinator` selects the state transition and event.

The stable code vocabulary is grouped by purpose:

- lifecycle and availability: `StateTransition`, `DependencyUnavailable`,
  `DevicesUnavailable`, `ProfileUnavailable`, `ReconnectWaiting`,
  `Reconnected`, and `ShutdownRequested`;
- calibration distinction: `NoPositionAvailable`,
  `PoorTranslationObservability`, and `BadRotationCalibration`;
- active health: `TrackerLost`, `TouchInputLost`, `VmtUnavailable`, and
  `SteamVrStopped`;
- unexpected adapter failure: `RuntimeFailure`; and
- application and cleanup: `ProfileApplied`, `ProfileApplyFailed`,
  `SafeDisableStarted`, `SafeDisableCompleted`, `SafeDisableFailed`,
  `RollbackCompleted`, and `RollbackFailed`.

`JsonLinesLtbLogSink` is the local append-only JSON Lines destination exposed
by `wizard --log <events.jsonl>`, `daily --log <events.jsonl>`, and
`wizard-demo --log <events.jsonl>`. It creates a missing parent directory,
appends one JSON object per event, and flushes each event. Omitting the option
disables the JSONL sink and creates no default event file. Log-write failures
are swallowed at the coordinator boundary so they cannot alter calibration or
prevent SafeDisable or rollback.

Wizard logging uses the same event envelope and records state transitions plus
the distinct calibration results `NoPositionAvailable`,
`PoorTranslationObservability`, and `BadRotationCalibration`. These codes keep
normal rotation-only selection separate from rejected rotation calibration.

If an unexpected adapter exception escapes during active daily use, the
coordinator first emits `RuntimeFailure` at error level with `exceptionType`
and `exceptionMessage` properties. It intentionally does not serialize a stack
trace. Only after that event does it run bounded SafeDisable across every active
surface and transition to `Stopped`; a cleanup failure remains independently
observable.

The event model has no telemetry transport. Logs remain local and may contain
device identities, configuration paths, and runtime observations. Export and
redaction are explicit operator actions, and raw logs, recordings, settings,
and backups are not repository or package inputs.

### Deployment decision

Version 0.1 is distributed as a self-contained, untrimmed, non-single-file
`win-x64` portable ZIP. Keeping separate files preserves app-local
`openvr_api.dll`, managed interop assemblies, and third-party licenses without
native extraction or trimming assumptions. The publish profile writes ignored
output under `artifacts/`; the packaging script stamps the version, verifies
the pinned OpenVR DLL and license, adds a release manifest, and emits a ZIP plus
SHA-256.

`RuntimeFrameworkVersion` is pinned to `8.0.28`, and the packager records both
that expected version and the actual runtime-pack version found in
`Ltb.App.deps.json`. It also records the .NET SDK, Python, and build/runtime
zlib versions used to make the archive. A .NET servicing update is intentional
release work: review the update, change the pin, rerun build/test/publish, and
repeat Windows runtime and hardware acceptance instead of allowing an
unreviewed runtime pack to float.

This is packaging, not an installer. It does not install or update SteamVR,
ALVR, VMT, drivers, firmware, or .NET, and it does not create shortcuts or a
background service. The bundle includes the .NET runtime. Signing, SmartScreen,
native launch, and live integration remain Windows release checks.

The complete product requirements remain in the [project
specification](specification.md); the implemented calibration details are in
[calibration.md](calibration.md).

## Milestone 5 capability-based device generalization

Milestones 1-4 could identify the initial Quest 2 Touch and Vive Tracker path,
but daily-use readiness still depended on one controller model tuple and
physical-pose selection depended primarily on the OpenVR tracker class.
Milestone 5 replaces that hardware lock-in with one centralized capability
inference and matching boundary. Model strings remain observations and profile
provenance; scattered model-name conditions are not application policy.

```text
OpenVR class, role, path, driver properties, optional input profile
                              |
                              v
             Ltb.OpenVr normalization and inference
                              |
                              v
      SteamVrDeviceDescriptor + SteamVrDeviceCapabilities
                /                  |                   \
               v                   v                    v
 Meta Touch input match   physical pose match   HMD observation
               \                   |                    /
                +---------- application policy --------+
                              |
                              v
          stable-identity/profile/calibration pipeline
```

`SteamVrDeviceCapabilities` reports the facts application policy needs:
`HasPosition`, `IsPhysicalPoseSourceEligible`, `IsVirtualPoseSource`,
`ControllerFamily`, `ControllerRuntime`, `ControllerModel`, and `InputProfile`.
The optional OpenVR input-profile path is retained in
`SteamVrDeviceMetadata` and normalized at the adapter boundary.
`CanUseAsPhysicalPoseSource` combines current connectivity, positional
capability, physical-source eligibility, and virtual-source exclusion so every
consumer applies the same minimum gate.

The central inference uses the OpenVR device category, driver and registered
device path, controller role, and a centralized Meta Touch input-profile table.
This gives each role one explicit acceptance rule:

| Role | Capability decision | Identity and safety constraints |
| --- | --- | --- |
| Meta Touch input controller | A connected input controller has a left/right role and matches a known Meta Touch family. When OpenVR supplies an input-profile path, it must also match that family. Orientation-only calibration may proceed when controller position is unavailable; full 6DoF still requires valid position samples. | Current controller runtime, family/model, optional input profile, role, and optional exact serial are runtime observations. A stored profile cannot make an unknown live descriptor supported. |
| Physical Lighthouse pose source | `CanUseAsPhysicalPoseSource` is true. A connected positional `GenericTracker` can satisfy this without a Vive-specific or Tundra-specific model check. | Exact stable serial remains the profile and reconnect key. VMT registered paths are marked virtual and excluded even though VMT also enumerates as `GenericTracker`, preventing a virtual output from following itself. |
| Lighthouse HMD | The connected `HeadMountedDisplay` at transient OpenVR index `0` must report positive Lighthouse driver, tracking-system, or actual-tracking-system evidence. No manufacturer or model allowlist is used, and LTB does not use an HMD as a physical controller-pose source. | SteamVR chooses the active display. `wizard` and `daily` reject Quest/ALVR/Meta/Oculus evidence across both tracking-system observations and fail closed on missing, duplicate, conflicting, or unknown active-HMD evidence. Windows verification must still prove each real HMD/runtime combination. |

Stable identity and capability answer different questions. The exact serial
answers whether a re-enumerated device is the same physical mount; capability
answers whether the current descriptor can safely fill the requested role.
Neither transient OpenVR indexes nor display names are persistence keys.

The application consumes inferred capabilities and observed controller
classification; it does not repeat OpenVR property normalization or model
tables. `Ltb.Configuration` continues to own profile validation and reuse.
Schema version 1 remains sufficient because `controller_runtime` and
`controller_model` already persist the observed controller classification used
by recalibration policy, while capability flags and the input-profile path are
current runtime observations rather than calibration data. Existing schema-1
profiles therefore require no migration. A future change that persists new
matching semantics must use normal schema versioning and compatibility tests
rather than silently changing schema-1 meaning.

Fake pipeline tests cover Quest 2, Quest 3 Touch Plus, and Quest Pro Touch
profiles plus Vive Tracker, Tundra Tracker, and generic eligible physical pose
sources through association, synthetic calibration, and schema-1 persistence
or reuse. Separate descriptor tests prove VMT virtual exclusion and exercise
the fail-closed active-display gate without a Lighthouse-HMD model allowlist.
HMDs do not participate in the calibration/profile pipeline. None of these
tests claims that ALVR exposes every input component or that any named
controller, pose source, or HMD combination works in a real SteamVR session.
That evidence remains in
[windows-verification.md](windows-verification.md).
