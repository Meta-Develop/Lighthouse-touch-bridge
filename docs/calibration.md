# Offline Calibration

## Problem and coordinate model

A static tracker/controller pose cannot distinguish the unknown relationship
between the Quest and Lighthouse worlds from the fixed tracker mount. The
offline solver removes that world transform by comparing motion between
synchronized samples.

```text
C -- X_mount = T_T_C --> T -- T_L_T(i) --> L -- Y = T_Q_L --> Q
controller               tracker              Lighthouse       Quest world

T_Q_C(i) = Y * T_L_T(i) * X_mount
effective mount: X_eff = A_tracker * X_mount * A_controller
runtime: T_L_output(t) = T_L_tracker(t) * X_eff
```

The notation `T_parent_child` means a transform from child coordinates to
parent coordinates. `Q`, `L`, `T`, and `C` are right-handed frames. Rotations
are normalized `System.Numerics` quaternions reported in `XYZW` component
order, positions are in meters, and timestamps are monotonic host seconds.

## Mount adjustment and effective runtime transform

The calibrated transform remains `X_mount = T_T_C`. Optional per-hand mount
adjustments produce a separate effective transform:

```text
X_eff = A_tracker * X_mount * A_controller
T_output(t) = T_tracker(t) * X_eff
```

Every factor is an active parent-from-child transform. A transform `T_A_B`
maps a point from child frame `B` into parent frame `A`:

```text
p_A = R_A_B * p_B + t_A_B
```

Composition keeps the parent transform on the left:
`T_A_B * T_B_C = T_A_C`. Thus the written product composes from the
left-hand parent toward the right-hand child, while a point is acted on by the
rightmost factor first. This is the left-to-right parent-composition contract.
`A_tracker` is the tracker-side correction immediately after the sampled
tracker pose and is therefore expressed in tracker-local axes. `A_controller`
is the controller-side correction after the calibrated mount and is expressed
in controller-local/output axes. The side names identify multiplication
position, not the left or right controller hand. Swapping these factors changes
both the rotation and the frame in which a translation acts.

Both adjustment slots default to the identity transform. With either identity,
that side leaves the other factors unchanged; with both identities,
`X_eff = X_mount` and the existing runtime result is preserved exactly.
Adjustment changes only the effective transform used for output placement. It
does not modify synchronized samples, lag estimation, the hand-eye equations,
solver thresholds, model selection, quality evidence, or the calibrated
`X_mount`.

### Human-entry Euler convention

Angles are a human-entry integration format only. A UI or other adapter must
accept degrees at its boundary, convert them to radians, and construct a
quaternion before calling portable core or configuration code. Euler angles
are neither a core transform representation nor a profile field.

For degree entries `(x_deg, y_deg, z_deg)`, use intrinsic local rotations in
the order `X`, then `Y`, then `Z`:

```text
x = x_deg * pi / 180
y = y_deg * pi / 180
z = z_deg * pi / 180

q_adjustment = Qx(x) * Qy(y) * Qz(z)
```

`Qx`, `Qy`, and `Qz` are right-handed active axis-angle quaternions about the
positive local axes. Under the active Hamilton convention used by
`RigidTransform`, this intrinsic local `XYZ` sequence is equivalent to an
extrinsic fixed-axis `ZYX` sequence. The quaternion product above is
authoritative when a math or UI library labels Euler conventions differently;
`Qz * Qy * Qx` would instead describe extrinsic fixed-axis `XYZ` (equivalently
intrinsic local `ZYX`) and is not interchangeable. The tracker-side slot
applies this convention at the tracker-local insertion point, while the
controller-side slot applies it at the controller-local insertion point. The
adapter passes the quaternion through `RigidTransform` for the core's finite
value checks and normalized representation. That finite unit quaternion, not
the original degree triplet, crosses the portable core/configuration boundary.
The desktop exposes these values per hand and per composition slot in
millimeters and degrees. Edits clamp the complete translation vector to the
portable `0.5 m` per-slot magnitude bound, reject non-finite input, apply live
through the App control plane, remain visibly dirty, and persist only after an
explicit **Save**. The effective transform shown by the UI is the
App-authoritative recomposition, not a second GUI math model.

## Staged solve

The input is an ordered array of already synchronized tracker/controller pose
pairs. Orientation-valid sample indices are divided first into disjoint solve
and held-out validation sets; relative motions are then constructed only
within their respective sample sets. This sample-level split prevents one
source pose from contributing to both fitting and validation.

For two samples `i` and `j`, the solver forms

```text
A = T_L_T(i)^-1 * T_L_T(j)
B = T_Q_C(i)^-1 * T_Q_C(j)
A * X_mount = X_mount * B
```

Motion pairs below the configured angular separation are discarded. Evenly
spaced anchor samples and a bounded pair count retain both local and
recording-wide motion baselines.

### Rotation

The rotation equation is

```text
R_A * R_X = R_X * R_B.
```

For each motion, the implementation adds
`L(q_A) - R(q_B)` to a quaternion homogeneous least-squares system. The unit
eigenvector of the smallest normal-matrix eigenvalue gives `R_X`; its sign is
canonicalized without changing the represented rotation. A residual-based
robust pass removes large rotation outliers and resolves the system.

Rotation observability is measured from the motion-axis tensor. The ratio of
its second-largest to largest eigenvalue must exceed the configured coverage
threshold, which rejects single-axis captures. Held-out geodesic residual RMS,
percentile, and robust inlier ratio then determine whether rotation is
accepted. Static motion, pure translation, insufficient orientation samples,
single-axis motion, timestamp violations, or failed held-out quality produce a
failed calibration. Auto mode does not conceal a failed rotation solve.

### Translation

After rotation passes, position-valid relative motions provide the linear
equation

```text
(R_A - I) * t_X = R_X * t_B - t_A.
```

The equations are stacked and solved by least squares while the accepted
`R_X` remains fixed. A robust residual pass can remove position outliers. The
translation normal matrix must have sufficient minimum eigenvalue and an
acceptable condition number. Two independent subsets of the solve samples
must also produce translations that agree within the stability threshold, and
the final magnitude must pass the physical plausibility gate.

On held-out position-valid motions, the full solution is compared with the
same accepted rotation and `t_X = 0`. Full 6DoF is accepted only when its
robust inlier ratio and position RMS pass and its absolute and fractional RMS
improvements both exceed their configured margins.

## Model selection and reported degeneracy

- `RotationOnly` validates rotation and always returns zero translation.
- `FullSixDof` requires every translation observability and quality gate; a
  translation rejection makes the calibration fail.
- `Auto` returns full 6DoF when all translation gates pass. If rotation is
  valid but translation is missing, unobservable, ill-conditioned, unstable,
  implausible, or fails held-out position quality, it returns the accepted
  rotation with zero translation and records the fallback reason.

The result separates rotation and translation observability and records a
machine-readable degeneracy reason. It also reports sample and motion-pair
counts, motion-axis coverage, translation condition number, rotation and
position residuals, robust inlier ratios, translation split disagreement, and
the selected `X_mount`.

## Configurable quality gates

`CalibrationOptions` owns the initial Milestone 0 defaults below. These values
are configuration, not permanent hardware-tuned constants.

| Gate | Default |
| --- | ---: |
| Minimum samples | 8 |
| Maximum selected motion pairs | 256 |
| Maximum paired timestamp difference | 1 microsecond |
| Accepted relative rotation | 2 to 170 degrees |
| Minimum motion-axis coverage | 0.04 |
| Maximum held-out rotation RMS | 2.5 degrees |
| Residual percentile | 95th |
| Held-out sample fraction | 25% |
| Minimum position-valid fraction | 60% |
| Minimum translation normal-matrix eigenvalue | `1e-4` |
| Maximum translation condition number | 500 |
| Maximum split translation disagreement | 5 mm |
| Maximum mount translation | 0.5 m |
| Maximum held-out position RMS | 40 mm |
| Minimum position RMS improvement | 0.5 mm and 2% |
| Minimum held-out inlier ratio | 70% |

## Deterministic synthetic validation

`Ltb.SyntheticData` generates paired streams from a known `X_mount`, an
arbitrary `Y = T_Q_L`, and the calibration composition equation. The random
seed is explicit and identical options reproduce identical streams and model
selection. The generator supports rotation and position noise, dropped
samples, variable rates, timestamp jitter, quaternion sign flips, pose
outliers, tracking-invalid samples, and partial controller-position validity.

The built-in scenarios are:

- `clean`: exciting multi-axis motion without injected measurement noise;
- `noisy`: seeded rotation and position noise, timestamp jitter, drops,
  variable sample intervals, and quaternion sign flips;
- `static`, `single-axis`, and `pure-translation`: rotation-degenerate captures
  that must fail rather than produce a transform;
- `translation-degenerate`: multi-axis rotation with insufficient valid
  controller positions, which allows Auto to demonstrate rotation-only
  fallback.

Raw synthetic controller timestamps contain the configured known lag (12 ms by
default). `AlignedPairs` instead assign tracker and controller the same
simulation-truth timestamp after applying that known lag. Consequently the
tests validate calibration after alignment and the raw-stream truth boundary;
they do not claim lag estimation.

## Command-line report

Run an end-to-end seeded scenario from the repository root:

```bash
dotnet run --project tools/Ltb.SyntheticData -- --scenario noisy --seed 20260717 --policy auto
```

Supported scenarios are `clean`, `noisy`, `static`, `single-axis`,
`pure-translation`, and `translation-degenerate`. Policies are `auto`,
`rotation`, and `full`. The report prints the verdict, requested and selected
models, selection reason, known-lag alignment boundary, truth and estimated
mounts, errors and residuals, observability and degeneracy verdicts, and sample
and injection counts. A failed calibration returns a nonzero process exit
code; an Auto rotation-only fallback is a successful reported outcome.

## Two-hand guided calibration

Milestone 3 composes the existing portable stages without forking their
numeric logic. For each hand, guided capture reports:

- orientation/tracking-valid and position-valid sample fractions;
- accumulated rotation and coordinate-invariant motion-axis coverage;
- separate rotation-ready and position-ready progress; and
- whether the rotation capture gate is accepted.

The left and right gestures are recorded separately while both candidate
tracker streams remain visible. `TrackerHandAssociator` compares angular-speed
magnitude, estimates lag for every viable hand/tracker candidate, and solves a
one-to-one serial assignment. It rejects disconnected or repeatedly invalid
candidates, weak correlation, ambiguous assignments, and inconsistent
left/right lag. Runtime device order and world-space direction never enter the
decision; a corrected swapped input order is reported explicitly.

After association, `PerHandCalibrationPipeline` runs the assigned raw streams
through `StreamLagEstimator`, `PoseStreamAligner`, and
`HandEyeCalibrationSolver` with `CalibrationPolicy.Auto`. The solver still
validates rotation first and holds it fixed while attempting translation.
Consequently:

- a bad rotation solve is a failed calibration and asks for another capture;
- missing controller position is a successful rotation-only fallback;
- insufficient translation observability or validation improvement is also a
  successful rotation-only fallback; and
- full 6DoF is selected only when all existing translation gates pass.

The quality report is per hand and retains lag, motion-axis coverage, rotation
RMS and percentile, position RMS and percentile where available, the
rotation-only position RMS comparison, translation condition, separate
rotation and translation inlier ratios, translation magnitude, split
disagreement, rotation/translation observability and degeneracy, and the exact
selection reason. These complete fields are emitted directly from the genuine
first-run `CalibrationResult`; the application does not recalculate quality or
select a model. Legacy schema 1 retains a smaller quality subset. Later-run
profile reuse therefore reports only persisted schema fields and never copies
RMS or the one persisted inlier ratio into missing percentile or
separate-inlier fields.

### Profile schema 3 and compatibility

Profile schema 3 retains the calibrated `tracker_to_controller` transform and
adds a required `mount_adjustment` object with two transform slots. The
relevant members are shown below; the other required profile fields are
unchanged and omitted:

```json
{
  "schema_version": 3,
  "mount_adjustment": {
    "tracker_side": {
      "translation_m": [0.0, 0.0, 0.0],
      "rotation_xyzw": [0.0, 0.0, 0.0, 1.0]
    },
    "controller_side": {
      "translation_m": [0.0, 0.0, 0.0],
      "rotation_xyzw": [0.0, 0.0, 0.0, 1.0]
    }
  }
}
```

The two stored transforms correspond to the portable
`TrackerSideAdjustment` and `ControllerSideAdjustment` values. Serialization
is deterministic: property order and component order are stable, quaternion
arrays are `XYZW`, translations are meters, and the existing atomic profile
store replacement remains the persistence boundary.

Each slot is validated independently. Every translation and quaternion
component must be finite, each rotation must be a unit quaternion, and the
Euclidean magnitude of each slot's translation must be at most `0.5 m`
inclusive. A valid tracker-side value does not compensate for an invalid
controller-side value, and limits are not checked only on the combined
`X_eff`. Missing schema-3 members, malformed arrays, non-finite values,
non-unit quaternions, and out-of-range translations fail closed.

Schema 2 remains a compatible input format. Loading a structurally valid
schema-2 profile supplies identity tracker-side and controller-side
adjustments, yielding `X_eff = X_mount`; the version difference alone must not
trigger recalibration. Normal reuse neither rewrites the profile as schema 3
nor requires a new capture. An explicit adjustment **Save** atomically upgrades
the active schema-2 pair to schema 3, while a failed Save leaves the live
effective snapshot and canonical profile bytes at their prior states. This
compatibility does not weaken parsing:
structurally malformed schema-2 data still fails closed rather than being
repaired or converted to identity.

Schema 1 remains a distinct legacy format. Reading it preserves its exact
legacy identity shape and smaller quality subset. Migration is explicit,
non-mutating, and reversible: it may add the supported driver/current-schema
shape only while preserving controller runtime, model, identity, calibrated
transform, quality, and provenance. It must not silently relabel an ALVR
profile as a Meta Link profile; cross-runtime reuse requires recalibration.

Profiles are keyed by tracker serial and semantic hand. A rotation-only profile
stores zero translation plus its fallback reason. A later run loads a complete
left/right pair by exact serial-and-hand match and applies it without capture;
a missing or mismatched side keeps the wizard on the first-run path. The
runtime also passes explicit-request, observed hand association, remount,
validation-threshold, controller runtime/model, expected-schema, and transform-
convention observations to `RecalibrationEvaluator`; the application does not
reimplement those trigger rules. Schema 2 compatibility is evaluated before a
schema-version trigger so a valid schema-2 profile does not route to capture
solely because schema 3 is current. An incompatible schema may route to
capture and replacement, while structurally malformed profile JSON stops
safely. Profile schema validation, deterministic JSON, and atomic save are
owned by `Ltb.Configuration`.

For production `daily` reuse, the runtime/model observation comes from the
current fail-closed ALVR gate, not from the stored profile: the local ALVR
version endpoint must respond and the current OpenVR controllers must pass the
Quest 2 Touch Miramar/`oculus_touch` tuple. Only then does the runtime report
`ALVR` and `Quest 2 Touch` for comparison with the stored values.

## Scripted wizard command

Run the Linux-safe deterministic two-hand flow from the repository root:

```bash
dotnet run --project src/Ltb.App -- wizard-demo --profiles <profile-store.json> [--log <events.jsonl>]
```

The fake session uses `CTRL-TEST-L`, `CTRL-TEST-R`, `LHR-TEST0001`, and
`LHR-TEST0002`. Tracker enumeration is reversed deliberately. The left stream
has valid controller position and selects full 6DoF; the right stream has valid
orientation but no controller position and selects normal rotation-only Auto
fallback. Run the command again against the same store to exercise serial-and-
hand reload and the no-capture apply path. Each hand emits deterministic
progressive coverage snapshots from growing sample prefixes, all evaluated by
the portable analyzer.

`--log` uses the same append-only JSON Lines sink as `daily`. Reusing a path
appends a new event sequence; omitting the option creates no event file. The
wizard records its state transitions and preserves separate codes for missing
controller position, poor translation observability, and bad rotation:
`NoPositionAvailable`, `PoorTranslationObservability`, and
`BadRotationCalibration`.

## Calibration diagnostics and runtime safety

Version 0.1 preserves four distinct observations because they require
different user actions and state transitions:

| Observation | Calibration meaning | State consequence |
| --- | --- | --- |
| Controller position unavailable | Rotation remains solvable, but translation has no input data | Successful Auto rotation-only profile with zero translation |
| Poor translation observability | Position exists, but the motion does not constrain a reliable lever arm | Successful Auto rotation-only profile with zero translation |
| Bad rotation calibration | The fixed mount rotation is unsupported or fails validation | Calibration failure; return to `Ready` with a retry diagnostic |
| Tracker lost | An active runtime pose source is unsafe or absent | `SafeDisable`, then wait for stable-serial reacquisition |

The first two results are normal model-selection outcomes. They retain the
accepted rotation and record different machine-readable fallback reasons. The
third result must not be converted into rotation-only success because every
runtime composition depends on a valid mount rotation. The fourth is not a
solver result at all: it is reported by runtime health monitoring and must not
be described as weak calibration data.

This distinction also applies to structured events. Calibration events record
capture, observability, validation, and selected-mode results. Runtime events
record source loss, SafeDisable, reacquisition, reapplication, and cleanup.
Tests and support tools should compare stable event codes and result fields
rather than infer the category from free-form prose.

## Recalibration, reuse, and rollback

A later run can reuse a profile only after exact stable-serial and semantic-hand
matching and the recalibration checks described above. Reconnect does not alter
the stored transform or bind it to a new transient OpenVR index. After the
required device returns, the daily-use coordinator passes through
`Ready -> ApplyProfile -> Active` again.

Failed recalibration does not make a partial capture the new active profile.
Both newly calibrated hands must validate before the profile store is replaced,
and both runtime applications must succeed before the pair is reported active.
If one application fails, the coordinator rolls back effects created by that
attempt. Rollback or cleanup failure is a runtime diagnostic requiring manual
inspection; it is not a reason to accept a lower-quality calibration.

Explicit first-party calibration may instead select only left or only right.
That path requires exactly one reusable opposite-hand profile but permits a
new selected hand with no prior selected-hand profile. It captures and scores
all viable current association contenders for only the requested hand, then
stages a store whose selected entry is added or replaces only the explicitly
known prior selected key. The opposite profile and unrelated serialized
objects remain byte-identical. Cancellation before the commit boundary leaves
canonical bytes unchanged and removes the stage; once commit begins, commit
wins over concurrent Stop/cancellation.

## Current limitations

The portable pipeline does not perform optional joint nonlinear refinement.
The `wizard-demo` command remains the deterministic fake demonstration path;
it proves orchestration, association, selection, reporting, persistence, and
reload on Linux without a live SteamVR runtime. The production `wizard` command
composes the live pipeline (override release -> Touch capture -> association ->
solve -> persist -> transactional apply -> Active) and is proven end-to-end
through injected fake production backends; its live execution still awaits
Windows hardware verification. The production `daily` command can
load an already complete two-hand store and apply it transactionally through
live OpenVR, VMT, and SteamVR-settings adapters, including watchdog,
SafeDisable, reconnect, and rollback policy. Automated transition tests use
fakes, so the Windows checklist remains required hardware acceptance for that
live later-run composition. Avalonia 11 is the selected desktop framework. The
GUI keeps the deterministic scripted demonstration available and invokes the
production wizard through the shared `Ltb.App` composition seam; native launch,
visual behavior, and live SteamVR hardware operation still require the Windows
verification checklist.
