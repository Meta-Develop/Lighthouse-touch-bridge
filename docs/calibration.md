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
runtime: T_L_output(t) = T_L_tracker(t) * X_mount
```

The notation `T_parent_child` means a transform from child coordinates to
parent coordinates. `Q`, `L`, `T`, and `C` are right-handed frames. Rotations
are normalized `System.Numerics` quaternions reported in `XYZW` component
order, positions are in meters, and timestamps are monotonic host seconds.

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

## Current limitations

Milestone 0 consumes synchronized pairs. It does not estimate lag, interpolate
or associate live streams, acquire poses from runtime APIs, or perform joint
nonlinear refinement. No OpenVR, ALVR, VMT, SteamVR override, recording, or UI
operation is part of the offline solver or synthetic command.
