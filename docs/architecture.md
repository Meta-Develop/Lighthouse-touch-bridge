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
has been selected: Milestone 1 adds no WinUI 3, WPF, or Avalonia dependency.
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

## Application boundary and open UI decision

`Ltb.App` remains a console composition and command-wiring boundary. No Windows
UI framework has been selected, and Milestone 1 introduces no WinUI, WPF, or
Avalonia dependency. A future framework choice must be recorded here before a
UI dependency is added.

The complete product requirements remain in the [project
specification](specification.md); the implemented calibration details are in
[calibration.md](calibration.md).
