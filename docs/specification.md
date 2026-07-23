# Lighthouse Touch Bridge

## Meta Quest Link Input and First-Party SteamVR Driver Specification

**Repository name:** `lighthouse-touch-bridge`

**Display name:** Lighthouse Touch Bridge

**Short name:** LTB

**Target platform:** Windows 10/11 x64 with SteamVR

**Target release:** first supported Meta Link and `driver_ltb` release

**Initial hardware path:** Bigscreen Beyond 2/2e, Meta Quest 2 with Quest 2
Touch controllers, two rigidly mounted Lighthouse-tracked devices, and
Lighthouse base stations

**External runtime dependencies:** SteamVR and official Meta Quest Link only

**License:** GPL-3.0-or-later

This document specifies the current target. It is not evidence that the target
has passed Windows, SteamVR, Meta runtime, or hardware verification. Automated
tests that run on Linux and Windows-only acceptance gates are identified
separately.

---

## 1. Executive Summary

Lighthouse Touch Bridge presents exactly two first-party SteamVR controller
devices. Each device combines:

- buttons, triggers, thumbsticks, and capacitive Touch state read from the
  installed Meta PC runtime by `Ltb.MetaLink`; and
- an authoritative pose computed from a rigidly mounted Lighthouse tracker.

The Quest connects through official Meta Quest Link or Air Link. LTB opens an
invisible LibOVR PC session to read Touch state. The Quest headset and its
controllers never register as SteamVR devices, and the Quest never becomes a
SteamVR HMD. Bigscreen Beyond remains the sole SteamVR HMD.

The runtime pose for each hand is

```text
T_output(t) = T_tracker(t) · X_mount
```

where `T_tracker` is sampled in OpenVR
`TrackingUniverseRawAndUncalibrated` space and `X_mount` is the calibrated
tracker-from-controller mount transform. `Ltb.App` composes the transform in
C# and sends the already-composed pose and complete Touch input state through
a versioned, same-user Windows named pipe. The first-party native SteamVR
driver `driver_ltb` consumes that feed and publishes the two controllers.

LTB supports three calibration policies:

1. **Rotation-only** estimates mount orientation and uses the tracker origin as
   the virtual controller origin.
2. **Full 6DoF** estimates mount orientation and translation from synchronized
   Meta Link Touch and Lighthouse tracker pose streams.
3. **Auto**, the default, accepts translation only when it is observable,
   plausible, stable, and better on held-out data; otherwise it retains the
   independently validated rotation-only solution.

Quest-to-Lighthouse playspace calibration is unnecessary. Relative rigid-body
motions eliminate the unknown transform between the LibOVR tracking origin and
OpenVR raw driver space.

ALVR, Virtual Motion Tracker (VMT), and SteamVR `TrackingOverrides` are not part
of the target architecture and are not accepted external dependencies. Legacy
code for that path is retained and runnable only behind warning-gated
`legacy-*` commands until the new Windows exit gates pass, and is then
scheduled for removal. It receives no new automation and must be removed or
isolated before the target release is complete.

---

## 2. Product Statement and Automation Boundary

> Meta Touch inputs. Lighthouse tracking. One SteamVR controller per hand.

LTB shall let a user attach one Lighthouse tracker rigidly to each Touch
controller, perform guided multi-axis motions, and then use those controllers
with a Lighthouse HMD without manually editing transforms, quaternions, device
indexes, driver manifests, or SteamVR settings.

LTB shall automate:

- SteamVR, Meta Quest Link, LibOVR ABI, active-HMD, controller, tracker,
  first-party driver, and IPC readiness checks;
- transactional `driver_ltb` registration, verification, rollback, and
  removal, with removal authority persisted as a registration receipt so
  removal survives application restarts;
- tracker discovery and left/right association by stable identity;
- synchronized recording and offline replay;
- Meta-to-application and OpenVR-to-application time alignment;
- rotation-only and optional full-6DoF mount calibration;
- held-out validation, Auto model selection, and quality reporting;
- per-hand profile persistence and recalibration decisions;
- runtime pose composition, input publication, heartbeats, reconnects, and
  fail-safe neutralization; and
- structured diagnostics and exportable calibration recordings.

LTB shall not install Meta software, install SteamVR, automate Quest setup with
ADB, keep the Quest awake automatically, carry Quest video, or launch a
headset-side application. The UI may give explicit manual instructions for
starting Quest Link or Air Link and keeping the headset and controllers awake.

---

## 3. Naming and Repository Topics

The repository slug is `lighthouse-touch-bridge`, the product name is
Lighthouse Touch Bridge, and the abbreviation is LTB.

- **Lighthouse** names the authoritative runtime tracking system.
- **Touch** names the controller input family.
- **Bridge** describes the explicit composition of two device sources through
  a first-party endpoint; it does not imply a general tracking stack.

Suggested repository description:

> Combine Meta Touch inputs from official Quest Link with Lighthouse-tracked poses in two first-party SteamVR controllers.

Suggested topics:

```text
steamvr
openvr
steamvr-driver
mixedvr
meta-quest
oculus-touch
vive-tracker
lighthouse
named-pipe
hand-eye-calibration
```

`alvr`, `vmt`, and `tracking-overrides` must not describe the current target.

---

## 4. System Concept and Ownership

```text
Quest + Touch
  -> official Meta Quest Link / Air Link
  -> installed LibOVR PC runtime, invisible session
  -> Ltb.MetaLink: full Touch inputs + calibration-time Touch poses
                                                       \
                                                        v
Lighthouse trackers -> SteamVR/OpenVR raw poses -> Ltb.App
                                               association, time alignment,
                                               calibration, composition
                                                        |
                                                        | IPC v1, same-user pipe
                                                        v
                                                  driver_ltb
                                             exactly two controllers
                                                        |
                                                        v
                                                   SteamVR
                                                        ^
                                                        |
                                          Bigscreen Beyond, sole HMD
```

The boundary responsibilities are:

| Component | Required ownership | Prohibited ownership |
|---|---|---|
| `Ltb.MetaLink` | Load the installed LibOVR runtime, create an invisible session, report readiness, and sample complete Touch input and pose state | SteamVR devices, calibration, tracker association, driver registration |
| `Ltb.OpenVr` | Discover the active HMD and physical trackers and sample tracker poses in raw/uncalibrated space | Meta runtime access or mount calibration |
| `Ltb.Calibration` | Deterministic association, lag estimation, hand-eye solve, validation, and model selection over runtime-independent models | UI, Meta, OpenVR, SteamVR, named-pipe, or driver dependencies |
| `Ltb.App` | Orchestrate setup, calibration, profile selection, pose composition, readiness, and publication | Native ABI layout or SteamVR server-driver internals |
| `Ltb.Protocol` | Define, encode, decode, and validate IPC v1 | Runtime SDK dependencies |
| `Ltb.Driver` | Own the managed driver feed, named-pipe client, health model, and transactional registration boundary | Calibration and Meta ABI access |
| `native/driver_ltb` | Validate IPC, apply freshness safety, and publish two SteamVR controller devices | Calibration, tracker discovery, Meta access, or pose composition |

Quest Link is a controller-state source, not a SteamVR integration. SteamVR
shall see no Quest HMD, no Meta-provided controller devices, no VMT devices,
and no substituted pose paths from LTB.

---

## 5. Coordinate, Transform, and Unit Contract

### 5.1 Frames

Define:

- `Q`: the LibOVR tracking-origin frame used during calibration;
- `D`: OpenVR raw/driver space, sampled with
  `TrackingUniverseRawAndUncalibrated`;
- `C`: the desired controller pose frame;
- `T`: the physical Lighthouse tracker frame;
- `Y = T_Q_D`: the unknown transform from raw driver space to the LibOVR
  tracking origin; and
- `X = T_T_C`: the fixed mount transform from tracker frame to desired
  controller frame.

`D` is the unseated, unstanding, uncalibrated tracking universe delivered at
the OpenVR driver boundary. The application shall neither apply a Standing nor
a Seated playspace transform to `T_D_T`, nor send such a transformed pose to
`driver_ltb`. The native driver shall publish the received pose with
world-from-driver rotation and translation equal to identity.

For a synchronized calibration sample `i`:

```text
T_Q_C(i) = Y · T_D_T(i) · X
```

For samples `i` and `j`:

```text
A_ij = T_D_T(i)^-1 · T_D_T(j)
B_ij = T_Q_C(i)^-1 · T_Q_C(j)
A_ij · X = X · B_ij
```

The unknown world transform `Y` cancels. Therefore the two worlds need not
share an origin or basis, and general Quest-to-Lighthouse playspace calibration
is out of scope.

At runtime:

```text
T_D_output(t) = T_D_tracker(t) · X
```

No Meta pose is used in the runtime pose equation.

### 5.2 Representation

All application, profile, and IPC transforms shall obey this contract:

| Property | Requirement |
|---|---|
| Handedness | Right-handed |
| Axes | `+X` right, `+Y` up, `-Z` forward |
| Translation | Meters |
| Linear velocity | Meters per second |
| Angles | Radians except human-readable quality reports may use degrees |
| Angular velocity | Radians per second |
| Quaternion storage | `(x, y, z, w)` |
| Transform meaning | Active parent-from-child transform |
| Point action | `p_parent = R_parent_from_child · p_child + t_parent_from_child` |
| Composition | Parent transform on the left; `T_D_T · T_T_C = T_D_C` |

Quaternions shall be finite and normalized before publication. Every adapter
shall name its source and destination frames. Handedness changes, axis changes,
unit conversion, quaternion reordering, inversion, and transform-order changes
must occur in one explicit, tested boundary; none may be implicit.

---

## 6. Calibration Modes

### 6.1 Rotation-Only

```text
X_rotation = [ R_X  0 ]
             [  0   1 ]

p_output(t) = p_tracker(t)
R_output(t) = R_tracker(t) · R_X
```

Rotation-only is a supported result when Meta position is absent, invalid,
stale, too noisy, or insufficiently excited; when the mount is close enough to
the desired origin; or when the user explicitly chooses it. It is not a
calibration failure.

### 6.2 Full 6DoF

```text
X_full = [ R_X  t_X ]
         [  0    1  ]

p_output(t) = p_tracker(t) + R_tracker(t) · t_X
R_output(t) = R_tracker(t) · R_X
```

Full 6DoF uses Meta position only as a temporary calibration reference. It
reduces lever-arm error when the tracker origin is displaced from the desired
controller origin.

### 6.3 Auto

Auto is the default and shall execute this staged pipeline:

1. Estimate and validate stream time alignment.
2. Solve rotation from orientation data alone.
3. Validate rotation independently on held-out samples.
4. If position is valid, evaluate translation observability.
5. Solve translation while holding the accepted rotation fixed.
6. Optionally refine rotation, translation, and lag with bounded robust
   optimization.
7. Compare rotation-only and full-6DoF candidates on held-out samples.
8. Select full 6DoF only when every translation quality gate passes.
9. Otherwise save and apply the accepted rotation-only result with the fallback
   reason.

Noisy Meta position must not corrupt a valid rotation result.

---

## 7. Why Optional Translation Matters

If the tracker origin and controller origin differ by `t_X`, the exact
controller position is:

```text
p_controller(t) = p_tracker(t) + R_tracker(t) · t_X
```

When a user rotates the controller about the grip, an offset tracker travels on
an arc. A rotation-only model reports that arc as controller-origin movement.
A full transform reconstructs the intended origin.

The effect varies with the mount:

- a tracker close to the intended origin may make translation unimportant;
- a tracker on the tracking ring or a long bracket can create visible position
  and aiming error under rotation; and
- translation calibration does not improve button input or the underlying
  Lighthouse measurements.

Translation is therefore an optional geometric improvement, never a
prerequisite for a usable rotation calibration.

---

## 8. Pose and Input Acquisition

Calibration recordings shall contain, per sample:

```text
Touch left/right:
  per-hand Meta timestamp, mapped application-monotonic timestamp,
  orientation, optional position, velocities, validity/tracking flags,
  complete buttons/touches/analog inputs

Tracker candidates:
  OpenVR timing metadata, mapped application-monotonic timestamp,
  raw-driver-space orientation and position, velocities,
  connectivity/tracking result/sample age, stable identity
```

`Ltb.MetaLink` shall use the public controller and tracking calls exposed by the
supported LibOVR C ABI. Each hand's pose timestamp is that hand's
`ovrPoseStatef.TimeInSeconds`. A session-wide or input-state
`SensorSampleTime` shall not replace the per-hand pose timestamp.

At startup and periodically thereafter, paired Meta-time and
`Stopwatch`/QueryPerformanceCounter samples shall establish and refresh a
mapping to the application's monotonic clock. Interpolation, lag estimation,
IPC timestamps, sample age, and freshness shall use mapped monotonic time,
never wall-clock or UTC time. The mapper shall expose uncertainty and reject
non-finite, regressing, or implausibly discontinuous mappings.

Tracker acquisition shall request `TrackingUniverseRawAndUncalibrated` and
preserve the runtime timing or prediction metadata needed to map the pose onto
the same application clock. Host arrival time may be recorded diagnostically,
but shall not silently replace an available source timestamp.

Recordings shall preserve validity, connectivity, tracking result, clock
mapping, and sample age. Quaternion sign continuity shall be normalized before
differentiation. Alignment shall use SLERP for orientation and linear
interpolation for position. Export and deterministic offline replay are
required before hardware calibration results can be treated as reproducible.

---

## 9. Automatic Tracker-to-Hand Association

Association shall not compare world-space directions because `Q` and `D` are
unrelated. Use coordinate-invariant angular-speed signatures:

```text
s(t) = ||omega(t)||
```

The guided flow shall:

1. ask the user to move only the left mounted controller;
2. align Meta and tracker angular-speed streams and select the tracker with the
   strongest valid correlation;
3. ask the user to move only the right mounted controller; and
4. confirm the remaining assignment and its correlation.

Association shall fail closed when both tracker candidates move similarly,
correlation is weak, lag is inconsistent, identity is ambiguous, or a candidate
is disconnected or repeatedly invalid. Profiles shall use a stable tracker
identity, not a transient OpenVR device index. Simultaneous-motion assignment
may be added later by solving the left/right correlation assignment matrix.

---

## 10. Stream Time Alignment

Meta Link Touch poses and OpenVR tracker poses have distinct sampling,
prediction, transport, and scheduling delays. After both clocks are mapped to
application-monotonic time, estimate residual lag with:

```text
lag_0 = argmax_tau corr(
    ||omega_touch(t)||,
    ||omega_tracker(t + tau)||
)
```

The alignment pipeline shall:

1. search a bounded coarse lag range by normalized cross-correlation;
2. solve provisional rotations for the strongest candidates;
3. refine lag at sub-frame resolution by minimizing a robust rotational
   hand-eye residual after interpolation;
4. report the accepted lag and confidence interval; and
5. compare left and right results and warn about unexplained disagreement.

Translation shall not be attempted before clock mapping, residual lag, and
rotation pass validation. Timing error during fast motion can otherwise appear
as mount translation or rotation error.

---

## 11. Rotation Solver

The relative rotations obey:

```text
R_A · R_X = R_X · R_B
```

The solver shall use many motion pairs with diverse axes and sufficient angular
separation:

1. Build pairs from synchronized samples.
2. Reject very small rotations and invalid intervals.
3. Select a balanced subset rather than every possible pair.
4. Compute a closed-form quaternion or SVD/Kronecker initial estimate.
5. Refine on `SO(3)` with a robust loss over geodesic residuals.
6. Evaluate on held-out samples not used by the solve.

The guided motion shall include rotations about at least two non-parallel axes.
The UI shall report excitation coverage rather than accepting a recording by
duration alone.

---

## 12. Translation Solver

After accepting `R_X`, the translation part of `A · X = X · B` gives:

```text
(R_A - I) · t_X = R_X · t_B - t_A
```

Stack valid pairs and solve with weighted least squares or a robust estimator.
Full translation is acceptable only when:

- Meta and tracker positions are valid for the selected intervals;
- rotations excite multiple axes;
- the stacked system has sufficient rank and an acceptable condition number;
- the translation magnitude is physically plausible for the mount;
- residuals are stable across splits or resampling; and
- held-out positional error improves over `t_X = 0` by a configured margin.

A numerical solution alone is insufficient. Observability and validation
determine acceptance.

---

## 13. Optional Joint Refinement

After the staged solution, the implementation may refine:

```text
X = [R_X, t_X]
residual lag = delta_t
```

The objective shall use separate scales for angular residuals in radians and
position residuals in meters. Refinement shall start from the accepted staged
solution, remain bounded, and be discarded if it worsens held-out rotation,
produces implausible translation, or increases timing uncertainty. The initial
target may ship with the staged closed-form and least-squares path without
joint refinement.

---

## 14. Model Selection and Quality Gates

When position is available, evaluate:

```text
Candidate A: X = [R_X, 0]
Candidate B: X = [R_X, t_X]
```

Required quality evidence includes rotation RMS and percentile error, position
RMS and percentile error, residual-lag confidence, motion-axis coverage,
translation condition number, inlier ratio, translation magnitude, and
split-recording stability.

Auto shall select full 6DoF only when rotation is accepted, position validity is
sufficient, translation is observable and plausible, held-out position improves
by the configured margin, and resampling or split tests are stable. Thresholds
shall be configurable and tuned from recorded hardware evidence rather than
declared permanent before measurement.

The profile and structured log shall record the selected mode and reason:

```json
{
  "selected_mode": "full_6dof",
  "selection_reason": "translation observable; held-out position RMS improved",
  "rotation_rms_deg": 1.2,
  "position_rms_mm": 8.4,
  "lag_ms": 11.5
}
```

---

## 15. First-Party `driver_ltb`

`driver_ltb` is the only target SteamVR controller endpoint. It shall register
exactly two devices: one stable left controller and one stable right
controller. It shall not register an HMD, tracker, tracking reference, or extra
controller. Both devices shall use a dedicated LTB input profile, stable
serials, and explicit left/right role hints.

The managed application shall send an already-composed controller pose in raw
OpenVR driver space. The native driver shall:

- validate protocol framing, values, session ordering, and freshness;
- publish pose, linear and angular velocity, buttons, capacitive states,
  trigger, grip, and thumbstick values;
- publish the pose with identity driver-to-world transform;
- neutralize invalid or stale input atomically; and
- report the device untracked instead of freezing the last valid pose.

The driver shall perform no calibration, association, clock mapping, Meta
runtime access, tracker sampling, pose composition, or profile selection.

Haptics are not supported initially. The driver shall create no haptic output
component and LTB shall not advertise haptic capability. Controller battery is
unavailable from the targeted public LibOVR ABI, so battery-present is false
and the value is ignored. A later battery source requires a versioned protocol
and capability change.

---

## 16. IPC v1 and Driver Registration

### 16.1 Transport and ordering

The producer and driver shall communicate through one local Windows named pipe
with a stable versioned name. The pipe server ACL shall permit only the owning
interactive user; remote clients and other users shall be rejected. Both ends
shall verify that the peer belongs to the expected same-user session. The
transport shall not use TCP, UDP, a remotely reachable pipe, or a shared
machine-wide unauthenticated endpoint.

All fields are little-endian. IPC v1 contains fixed-layout `HandState` and
`Heartbeat` packets:

| Segment | Fields |
|---|---|
| Header | ASCII magic `LTB1`, major `1`, minor `0`, message type, zero reserved bits, payload length |
| Ordering | unpredictable nonzero 128-bit session identifier, 64-bit sequence, producer monotonic nanoseconds |
| Identity | left/right hand and presence/validity/tracked flags |
| Pose | position `(x,y,z)` and quaternion `(x,y,z,w)` in raw driver space |
| Motion | linear velocity `(x,y,z)` and angular velocity `(x,y,z)` |
| Digital input | buttons and capacitive-touch bitsets |
| Analog input | trigger, grip, and stick `(x,y)` |
| Optional telemetry | battery-present flag and battery fraction |

Every producer start or transport reconnection shall create a new unpredictable
nonzero session identifier and reset sequence to zero. The sequence is one
global cross-message order within a session: every heartbeat, left state, and
right state consumes the next value. A new session is accepted only at sequence
zero. Its first timestamp may be any nonzero monotonic value; it is not compared
with the retired session's timestamps. Retired sessions and replayed or
out-of-order global sequences shall be rejected.

`producer monotonic nanoseconds` means heartbeat send time for a `Heartbeat`
packet and mapped final-pose sample time for a `HandState` packet. Timestamp
non-regression is enforced separately for heartbeat, left-hand state, and
right-hand state within the active session, not across unlike message streams.
This permits a hand pose sampled before a heartbeat to arrive on the next
global sequence without being rejected merely because its sample timestamp is
older than that heartbeat's send timestamp.

The producer shall send periodic heartbeats even when controller state does not
change. If no valid state or heartbeat arrives for 500 ms, the driver shall
mark both devices untracked and atomically neutralize every button, touch,
trigger, grip, and stick. Invalid packets shall not refresh freshness. A fresh
valid packet may recover only under the active session and ordering rules; a
reconnect begins a new session at sequence zero.

Both implementations shall reject bad magic, unsupported version or message
type, bad length, truncation or trailing bytes, nonzero reserved bits, empty
session identifiers, invalid hand/flag/bit ranges, non-finite numbers,
out-of-range analog or battery values, degenerate or non-unit quaternions,
inconsistent validity flags, replayed sequences, and per-stream regressing
timestamps. A rejected packet shall never partially update either hand.

### 16.2 Registration

`Ltb.Driver` shall install and register the first-party external driver through
`vrpathreg` or the documented `openvrpaths.vrpath` boundary. The operation
shall:

1. resolve the intended driver root and reject build or source directories that
   lack the staged manifest and binary;
2. snapshot the existing external-driver registration and relevant
   `activateMultipleDrivers` value;
3. register the LTB driver and set `activateMultipleDrivers = true` when
   required;
4. re-read state and verify the exact canonical driver path;
5. roll back the snapshot if any step fails; and
6. on removal, remove only the LTB path and restore the prior setting without
   deleting unrelated drivers or user configuration.

Registration shall persist its snapshot as a durable registration receipt so
removal authority survives application restarts; removal takes the prior
`activateMultipleDrivers` presence and value from that receipt.

A SteamVR restart may be required after registration or driver replacement.
LTB shall report that requirement explicitly and shall not claim readiness
until SteamVR loads the expected driver build and exposes exactly the two LTB
controllers.

---

## 17. Meta Quest Link and LibOVR Contract

Official Meta Quest Link or Air Link is the only target source for Touch state.
The user supplies a compatible installed Meta PC runtime. LTB shall not
redistribute Meta headers, libraries, DLLs, installers, or other runtime
binaries.

The supported baseline is the official Oculus PC SDK package version `32.0.0`,
whose public C ABI version is `1.64`:

- runtime DLL ABI major: `1`;
- x64 runtime DLL name: `LibOVRRT64_1.dll`; and
- requested minor ABI version: `64`.

`Ltb.MetaLink` shall load the DLL from the complete installation path derived
from the installed Meta runtime registration. It shall not rely on the process
search path, current directory, or a filename-only load. Initialization shall
request the public C ABI minor version 64 and an invisible session. The adapter
shall verify every x64 ABI struct size and field offset it consumes before
sampling.

The runtime roles are:

- Quest Link or Air Link keeps the Quest and Touch controllers connected to the
  Meta PC runtime;
- `Ltb.MetaLink` reads full public Touch inputs and calibration-time poses;
- SteamVR receives neither the Quest HMD nor Meta-native controller devices;
- `driver_ltb` is the only controller presentation path; and
- Bigscreen Beyond remains the sole SteamVR HMD.

The adapter shall expose readiness as `NotInstalled`, `AbiUnavailable`,
`RuntimeStopped`, `HeadsetDisconnected`, `ControllersUnavailable`, `Ready`, or
`Faulted`, with a remediation diagnostic. Only `Ready` permits publication.
Loss of runtime readiness shall neutralize inputs, mark both LTB controllers
untracked, stop the old feed session, and require recovery through a new IPC
session.

Per-controller battery state is unavailable from this public ABI and shall be
reported as absent. LTB shall not infer battery from unrelated headset or
runtime values.

LibOVR is deprecated and may disappear or become incompatible in a future Meta
runtime. This is an accepted platform risk, mitigated by a narrow
`Ltb.MetaLink` interface, exact ABI layout tests, a deterministic fake source,
path/version/readiness diagnostics, and the ability to add a future alternative
official controller source without changing calibration or IPC models.

---

## 18. Application State, Readiness, and Health

The target state machine is:

```text
Stopped
  -> DependencyCheck
  -> WaitingForSteamVR
  -> WaitingForMetaLink
  -> WaitingForTrackers
  -> WaitingForDriver
  -> Ready
  -> Recording
  -> Association
  -> TimeAlignment
  -> RotationSolve
  -> TranslationAttempt
  -> Validation
  -> SaveProfile
  -> StartingFeed
  -> Active
```

Recovery transitions are:

```text
Any calibration state + recoverable failure -> Ready with diagnostic
Active + one tracker lost                -> affected hand untracked and neutral
Active + Meta readiness lost             -> both hands untracked and neutral
Active + pipe/driver stale or disconnected-> both hands untracked and reconnect
Active + SteamVR stopped                 -> Stopped
Recovered transport or runtime           -> new IPC session, then Active
Unrecoverable fault                       -> Faulted until user action
```

Readiness shall be an explicit conjunction of:

- supported Windows x64 environment;
- SteamVR running with the intended Lighthouse HMD as the sole HMD;
- Meta runtime installed, ABI-compatible, linked, and reporting both Touch
  controllers;
- two distinct, connected physical tracker identities selected by saved
  left/right controller profiles or the current exact-two association;
  unrelated physical trackers may coexist and shall not participate in
  controller publication;
- matching valid profiles or a completed calibration;
- `driver_ltb` registered and loaded as exactly two controllers; and
- current same-user IPC and heartbeat health.

Health snapshots and structured logs shall distinguish installation, ABI,
runtime, headset, per-controller input, per-tracker pose, clock mapping, feed,
driver, profile, and calibration failures. They shall report last valid sample
age, current IPC session, last accepted sequence, heartbeat age, reconnect
attempts, and the exact reason a hand is neutral or untracked. “No Meta
position” and “translation unobservable” are normal rotation-only outcomes;
bad rotation calibration is a calibration failure.

---

## 19. Profile Format

Profiles are keyed by hand, controller runtime/model, tracker stable identity,
and mount identity where available. Example:

```json
{
  "schema_version": 2,
  "profile_name": "Quest 2 Touch + Vive Tracker mount A",
  "hand": "left",
  "controller_runtime": "meta_link_libovr",
  "controller_model": "Quest 2 Touch",
  "controller_identity": "optional-runtime-identifier",
  "tracker_serial": "LHR-XXXXXXXX",
  "driver_profile": "ltb_touch",
  "calibration_policy": "auto",
  "selected_mode": "full_6dof",
  "tracker_to_controller": {
    "translation_m": [0.014, -0.052, 0.031],
    "rotation_xyzw": [0.012, -0.704, 0.019, 0.710]
  },
  "estimated_lag_ms": 11.5,
  "quality": {
    "rotation_rms_deg": 1.2,
    "position_rms_mm": 8.4,
    "translation_condition": 14.7,
    "inlier_ratio": 0.94
  },
  "selection_reason": "translation observable; held-out position RMS improved",
  "created_utc": "2026-07-17T00:00:00Z"
}
```

UTC is allowed for human-facing profile provenance only; runtime alignment and
freshness use monotonic time. Rotation-only profiles store zero translation and
the reason translation was not selected. Schema migration shall be explicit
and reversible; target code shall not silently relabel an `ALVR` profile as a
Meta Link profile because its source timing and controller identity contracts
differ.

---

## 20. User Experience

### 20.1 First run

1. Detect SteamVR and the official Meta Quest Link installation.
2. Verify LibOVR ABI 1.64 availability without redistributing its DLL.
3. Install or verify `driver_ltb` transactionally and request a SteamVR restart
   if needed.
4. Ask the user to start Quest Link or Air Link manually and keep the headset
   and controllers awake; do not use ADB.
5. Verify that Bigscreen Beyond is the sole SteamVR HMD and that no Quest HMD or
   Meta-native controller device has entered SteamVR.
6. Open the invisible Meta session and show per-runtime and per-hand readiness.
7. Discover the two controller-mounted Lighthouse trackers by stable identity.
   Reuse may select them from additional raw Lighthouse trackers; a new
   association/calibration capture requires exactly two candidates.
8. Ask the user to keep the Touch controllers observable by Quest cameras when
   full 6DoF is desired.
9. Guide separate left and right pitch, yaw, roll, and moderate translation
   motions while displaying tracking validity and excitation coverage.
10. Associate, align, solve, and validate each hand.
11. Display `Full 6DoF` or `Rotation-only`, the reason, and quality evidence.
12. Save profiles, start a new IPC session, and verify exactly two live LTB
    controllers in SteamVR.

Every missing dependency or readiness failure shall include a direct manual
remediation. The user shall not need to edit JSON, driver paths, transforms,
quaternions, or device indexes.

### 20.2 Later runs

1. Verify SteamVR, Bigscreen Beyond, Quest Link, Meta controllers, trackers,
   `driver_ltb`, and the same-user pipe.
2. Load profiles matching both selected controller-source tracker identities
   and the Meta Link controller runtime. Ignore unrelated raw Lighthouse
   trackers, including full-body trackers.
3. Start a fresh feed session and wait for valid two-hand readiness.
4. Publish complete state and monitor clocks, trackers, Meta runtime, pipe,
   driver watchdog, and SteamVR.
5. Recover through a new session after disconnect; never reuse a stale session
   or frozen pose.

### 20.3 Recalibration triggers

- explicit user request;
- tracker moved to another hand;
- physical mount moved;
- controller model or controller-source contract changed;
- quality check exceeds its threshold; or
- transform convention or profile schema changed incompatibly.

---

## 21. Target Scope, Non-Goals, and Hardware Path

### 21.1 Required target scope

- Windows desktop or tray application on .NET 8 or later;
- official Meta Quest Link/Air Link controller ingestion through the installed
  LibOVR PC runtime and an invisible session;
- first-party `driver_ltb` exposing exactly two SteamVR controllers;
- OpenVR raw/uncalibrated tracker discovery and pose acquisition;
- serial-based association, monotonic clock mapping, residual-lag estimation,
  rotation-only and full-6DoF calibration, Auto selection, and held-out quality
  reporting;
- C# composition of `T_output = T_tracker · X_mount`;
- versioned same-user named-pipe IPC, session ordering, heartbeat, reconnect,
  500 ms watchdog, and neutral fail-safe behavior;
- transactional driver registration and rollback;
- profile persistence, structured logs, and exportable/replayable recordings;
  and
- deterministic managed and native tests plus recorded Windows acceptance
  evidence.

### 21.2 Non-goals

- ALVR, VMT, or SteamVR `TrackingOverrides` as an installed dependency or
  supported end-state path;
- registering Quest as a SteamVR HMD or controller provider;
- replacing Bigscreen Beyond as the active HMD;
- headset-side LTB software, video streaming, standalone Quest operation, or
  general Quest-to-Lighthouse playspace calibration;
- haptics in the initial driver protocol;
- automatic Meta or SteamVR installation, ADB setup, controller keep-awake,
  firmware management, or automatic hardware configuration;
- cloud accounts, telemetry, or remote IPC; and
- support for every controller or tracker family in the first target release.

The initial supported path is:

```text
Bigscreen Beyond 2/2e as the sole SteamVR HMD
+ Quest 2 connected through official Meta Quest Link or Air Link
+ Quest 2 Touch controllers visible only to the Meta PC runtime
+ two rigidly mounted Vive Trackers
+ Lighthouse base stations
+ first-party Ltb.MetaLink, Ltb.Driver, and driver_ltb
```

The architecture shall use generic source interfaces so later Meta Touch,
Lighthouse tracker, and Lighthouse HMD variants can be qualified without
changing the calibration mathematics.

Legacy ALVR/VMT/`TrackingOverrides` code remains buildable and runnable only
behind warning-gated `legacy-*` commands while the first-party path is
completing its Windows exit gates, and is scheduled for removal afterwards. It
shall receive no new setup, configuration, recovery, packaging, or daily-use
automation. No legacy external software is accepted in the release end state.

---

## 22. Technology and Project Layout

The target stack is C#/.NET 8 or later for orchestration and calibration, C++20
for the portable/native SteamVR driver, OpenVR for SteamVR client and server
driver boundaries, JSON with schema versioning for profiles, and structured
logging. The selected Windows UI framework remains an architecture decision
recorded in `docs/architecture.md`; this specification does not choose one.

Target layout and dependencies:

```text
/src
  /Ltb.App             orchestration, composition, state machine
  /Ltb.Core            runtime-independent pose and recording models
  /Ltb.Calibration     deterministic hand-eye calibration
  /Ltb.Configuration   schema-versioned profiles and settings
  /Ltb.MetaLink        narrow LibOVR source and fake
  /Ltb.OpenVr          raw tracker and active-HMD boundaries
  /Ltb.Protocol        runtime-independent IPC v1 codec/validation
  /Ltb.Driver          managed feed, named pipe, health, registration
  /Ltb.Gui             desktop presentation layer
/native
  /driver_ltb          portable protocol/watchdog core and Windows OpenVR shell
/tests
  /Ltb.Calibration.Tests
  /Ltb.Configuration.Tests
  /Ltb.MetaLink.Tests
  /Ltb.Protocol.Tests
  /Ltb.Driver.Tests
  /Ltb.Integration.Tests
/tools
  /Ltb.RecordingInspector
  /Ltb.SyntheticData
/docs
  architecture, calibration, setup, troubleshooting, verification
```

`Ltb.Calibration` shall depend only on portable pose/math models and shall
remain deterministic under offline replay. LibOVR structs shall not escape
`Ltb.MetaLink`. OpenVR driver structs shall not escape the native shell.
Protocol messages shall carry only normalized application models.

---

## 23. Verification Plan

### 23.1 Linux-automated managed tests

Linux CI can prove portable logic but not Windows runtime behavior. It shall
run build and test coverage for:

- synthetic recovery of known rotation, translation, unknown world transform,
  and lag under noise, drops, variable rate, jitter, sign flips, outliers,
  discontinuities, degenerate motion, and partial position;
- association, clock mapping, interpolation, lag confidence, held-out model
  selection, and deterministic profile serialization;
- a fake Meta source covering all readiness states, per-hand timestamps,
  complete input mapping, reconnects, invalid data, and absent battery;
- exact x64 LibOVR ABI structure sizes and field offsets as data-level contract
  tests, without claiming the installed Windows DLL was loaded;
- IPC golden bytes and cross-language fixtures;
- malformed protocol cases: magic/version/type/length/reserved fields,
  truncation/trailing bytes, invalid enums/bits/flags, NaN/infinity, analog
  ranges, quaternion norm, zero/new/retired sessions, replay/order, and
  per-stream timestamp regression;
- atomic rejection, session rollover, heartbeat, 500 ms timeout,
  neutralization, reconnect, and fake-pipe failures; and
- coordinate axes, quaternion order, raw-driver-space pose, zero translation,
  full translation under rotation, and `T_tracker · X_mount` composition.

### 23.2 Native portable tests

Linux CI shall build the portable C++ protocol and state core without the
Windows OpenVR shell and run CTest:

```text
cmake -S native/driver_ltb -B <build-dir> -DLTB_BUILD_OPENVR_DRIVER=OFF -DLTB_BUILD_TESTS=ON
cmake --build <build-dir>
ctest --test-dir <build-dir> --output-on-failure
```

Native tests shall decode the same managed golden packets, reject the same
malformed corpus, enforce the same session/order/range rules, and prove the
watchdog and neutral fail-safe behavior. Generated build directories and
binaries are not source artifacts.

### 23.3 Windows software gates

Windows CI or a controlled Windows test host shall additionally prove:

- x64 managed/native builds and the staged driver manifest/resource layout;
- named-pipe same-user ACL and session checks, local-only access, disconnect,
  reconnect, cancellation, and clean shutdown;
- cross-language ABI/golden-packet compatibility;
- transactional `vrpathreg` registration, exact-path verification, unrelated
  driver preservation, rollback, removal, and restoration of
  `activateMultipleDrivers`; and
- SteamVR loading the intended `driver_ltb` binary and exposing exactly two
  correctly profiled controller roles with no HMD or extra device.

### 23.4 Windows Meta/SteamVR hardware exit gates

These gates require the official installed runtimes and physical hardware and
cannot be replaced by Linux fakes:

- resolve and load the installed `LibOVRRT64_1.dll` by its complete registered
  path, initialize an invisible session requesting minor 64, and record ABI
  1.64 success;
- observe left and right `ovrPoseStatef.TimeInSeconds`, clock mapping, complete
  inputs, tracking validity, sleep/wake, Link disconnect, and reconnect;
- prove Quest Link supplies state while Quest and Meta-native controllers do
  not enter SteamVR and Bigscreen Beyond remains the sole HMD;
- observe the two selected controller-source tracker streams in
  raw/uncalibrated space while ignoring unrelated raw trackers;
- record, replay, calibrate, and validate rotation-only and full-6DoF mounts at
  varied offsets, orientations, motion rates, and partial Quest occlusion;
- verify static alignment, rapid pitch/yaw/roll, in-place wrist rotation,
  aiming/tool alignment, complete supported inputs, repeated startup, profile
  reuse after reboot, and remount/recalibration behavior;
- verify `driver_ltb` pose/input timing, left/right role and profile bindings,
  no haptic capability, absent battery, reconnect session rollover, tracker
  loss, Meta loss, pipe loss, 500 ms stale transition, and neutral recovery;
  and
- verify install, update, rollback, removal, and SteamVR restart instructions
  on a machine with unrelated external drivers and settings.

Results from the first three groups do not constitute hardware verification.
The release record shall identify the exact runtime versions, hardware,
commands, observations, failures, and retained evidence for the Windows gates.

---

## 24. Milestones

### Milestone 0 — Coordinate and offline calibration proof

- freeze frame, axis, unit, quaternion, timestamp, and transform contracts;
- generate synthetic paired Meta and raw tracker streams;
- recover rotation, translation, unknown world transform cancellation, and lag;
- detect degenerate translation and preserve rotation-only fallback; and
- produce deterministic quality reports.

### Milestone 1 — Meta Link and raw tracker acquisition

- implement the narrow LibOVR ABI 1.64 adapter and layout guards;
- implement fake Meta source and clock mapping;
- acquire raw/uncalibrated tracker streams;
- export and replay recordings; and
- pass the Windows Meta runtime acquisition gates.

### Milestone 2 — Protocol and one-hand first-party driver

- freeze IPC v1 and cross-language golden packets;
- implement managed feed, same-user pipe, session/sequence/heartbeat rules;
- implement portable native parser/state/watchdog and CTest;
- publish one already-composed controller pose and complete input set in a
  development build; and
- validate stale neutralization and registration rollback.

### Milestone 3 — Exactly two controllers and calibration wizard

- register stable left/right devices with the dedicated profile;
- implement tracker association, guided capture, Auto selection, and profiles;
- verify Bigscreen Beyond remains sole HMD and Quest never enters SteamVR; and
- complete two-hand Windows integration checks.

### Milestone 4 — Reliable daily use and release gates

- implement startup sequencing, readiness, health, reconnect, watchdog,
  transactional install/update/remove, diagnostics, packaging, and docs;
- complete every Windows software and hardware exit gate;
- remove target packaging and setup reliance on ALVR, VMT, and
  `TrackingOverrides`; and
- either remove legacy code or retain it only as clearly unsupported,
  compile-only migration code with no external dependency in the release.

### Milestone 5 — Qualified generalization

- qualify newer Meta Touch controllers and additional Lighthouse trackers/HMDs
  using recorded evidence;
- evaluate an alternative official controller source if LibOVR compatibility
  ends; and
- version protocol capabilities only when a demonstrated requirement, such as
  haptics, cannot fit IPC v1 safely.

---

## 25. Definition of Done

The target release is complete only when a Windows user can:

1. Run Bigscreen Beyond 2/2e as the sole SteamVR HMD.
2. Connect Quest 2 through official Meta Quest Link or Air Link without Quest
   or Meta-native controller devices entering SteamVR.
3. Use the installed LibOVR runtime through an invisible ABI 1.64 session,
   without redistributed Meta binaries.
4. Install and register `driver_ltb` transactionally and see exactly two LTB
   controllers with stable left/right roles and the dedicated profile.
5. Attach and discover one stable Lighthouse tracker identity per controller,
   while allowing unrelated Lighthouse trackers to remain connected without
   changing the selected pair.
6. Perform the guided multi-axis motions and receive automatic hand
   association and time alignment.
7. Receive a held-out validated rotation solution for each hand.
8. Receive full translation when Meta position and motion make it observable,
   or a reasoned rotation-only fallback when they do not.
9. Publish `T_output = T_tracker · X_mount` from C# in raw driver space through
   IPC v1 and use supported Touch inputs with the tracker-derived pose.
10. Restart and reconnect through new sessions while preserving matching
    profiles and monotonic ordering.
11. Lose a tracker, Meta runtime, pipe, or producer without a frozen hand: by
    500 ms stale age, affected output is untracked and all stale inputs are
    neutral.
12. Operate without ALVR, VMT, `TrackingOverrides`, haptics, ADB automation,
    cloud accounts, or remote services.
13. Export logs and recordings sufficient to reproduce a calibration or
    protocol failure.
14. Produce recorded evidence that every Windows software and hardware gate in
    section 23 passed on the supported hardware path.

Passing Linux builds and tests alone does not satisfy this Definition of Done.
Until item 14 is recorded, the implementation is a target or unverified build,
not a hardware-verified release.

---

## 26. Project Assessment

The project remains a focused utility, but its supported architecture now
includes a narrow native SteamVR driver because reliable input/pose composition
and fail-safe ownership require one first-party endpoint. The difficult work is
not only the hand-eye mathematics; it is the set of explicit integration
contracts around:

- an installed, deprecated LibOVR ABI and per-hand time mapping;
- two unrelated tracking frames and clocks;
- raw-driver-space transform composition;
- cross-language protocol validation and freshness;
- exactly-two-device SteamVR presentation;
- transactional driver registration; and
- observable recovery that never freezes stale pose or input.

The design contains that risk by keeping Meta access, OpenVR acquisition,
calibration, IPC, managed driver lifecycle, and native presentation in narrow
modules with deterministic fakes and shared golden data. The most important
remaining uncertainty is Windows behavior with the official Meta runtime,
SteamVR, Bigscreen Beyond, Touch controllers, and mounted Lighthouse trackers.
That uncertainty must be closed with the section 23 exit gates; it must not be
converted into an implementation-complete or hardware-verified claim based on
portable tests.
