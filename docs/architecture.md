# Architecture

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
Precise upstream URLs, Git blob IDs, and hashes are retained in the
third-party [README](../src/Ltb.OpenVr/ThirdParty/OpenVR/README.md).

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

`Ltb.App` owns console command wiring and composition only. No GUI framework
has been selected: Milestones 1-2 add no WinUI 3, WPF, or Avalonia dependency.
A future desktop framework choice must be recorded in this document before the
dependency is introduced.

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
verify with fake probe / defer live provenance -> monitor all health sources
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
valid `<driver>/<device>` value to `/devices/<driver>/<device>`. A fresh VMT
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

On managed exits, SafeDisable is fail-closed and best-effort in two independent
steps. It first sends the disabled Joint configuration, then attempts exact
settings release even if VMT deactivation failed. Cleanup failures are returned
and produce a distinct command exit code rather than being hidden. Touch
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

Milestone 2 adds no GUI framework, two-hand calibration wizard, automatic
association workflow, or Milestone 4 reconnect, installer, and daily-use
expansion beyond the explicitly required safety and settings recovery. Those
boundaries remain unchanged.

## Application boundary and open UI decision

`Ltb.App` remains a console composition and command-wiring boundary. No Windows
UI framework has been selected, and Milestones 1-2 introduce no WinUI, WPF, or
Avalonia dependency. A future framework choice must be recorded here before a
UI dependency is added.

The complete product requirements remain in the [project
specification](specification.md); the implemented calibration details are in
[calibration.md](calibration.md).
