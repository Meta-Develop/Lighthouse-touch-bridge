# Lighthouse Touch Bridge

## Adaptive Rotation-Only / Full 6DoF Calibration Concept and Implementation Specification

**Repository name:** `lighthouse-touch-bridge`
**Display name:** Lighthouse Touch Bridge
**Short name:** LTB
**Target platform:** Windows 10/11, SteamVR
**Initial target setup:** Bigscreen Beyond 2/2e, Meta Quest 2 with Quest 2 Touch controllers, two Lighthouse-tracked devices, ALVR, and Virtual Motion Tracker (VMT)
**Recommended license:** MIT

---

## 1. Executive Summary

Lighthouse Touch Bridge is a small Windows coordinator that combines two independent controller data sources into one usable SteamVR controller per hand:

- **Meta Touch controllers** provide buttons, triggers, sticks, capacitive states, and optionally haptics.
- **Lighthouse-tracked devices** provide the authoritative runtime position and orientation.

The initial hardware implementation uses one Vive Tracker rigidly mounted to each Quest 2 Touch controller. The architecture should not be hard-coded to Bigscreen Beyond or HTC hardware; later versions may support other Lighthouse HMDs and generic Lighthouse-tracked devices.

The runtime pose is:

```text
T_output(t) = T_tracker(t) · X_mount
```

where `X_mount` is the fixed transform from the tracker frame to the desired SteamVR controller frame.

LTB supports three calibration policies:

1. **Rotation-only**
   - Estimate tracker-to-controller rotation.
   - Set mount translation to zero.
   - Use the physical tracker origin as the virtual controller position origin.

2. **Full 6DoF**
   - Estimate both rotation and translation from synchronized Tracker and Quest Touch pose streams.
   - Align the virtual controller origin with the controller pose exposed by ALVR during calibration.

3. **Auto** — the default
   - Always solve rotation independently.
   - Attempt translation only when Quest position is valid and the captured motion makes translation observable.
   - Apply the full transform only when held-out validation shows that it is stable and materially better than the rotation-only model.
   - Otherwise fall back to rotation-only without failing the entire calibration.

Quest-to-Lighthouse global space calibration is not required. The unknown world-to-world transform is eliminated by using relative rigid-body motions and solving a hand-eye calibration problem.

After calibration, Quest controller pose is not needed for runtime tracking. Quest 2 and ALVR remain connected only to keep the Touch input devices alive and deliver controller inputs.

A custom SteamVR driver is not required for the first release. The initial integration path is:

```text
ALVR tracking-reference-only
+ OpenVR pose sampling
+ LTB calibration and orchestration
+ VMT local transform
+ SteamVR TrackingOverrides
```

---

## 2. Product Statement

> Meta Touch inputs. Lighthouse tracking. One SteamVR controller.

LTB should let a user mount a Lighthouse tracker on each Touch controller, perform one guided multi-axis motion, and then use the controllers with a Lighthouse HMD such as Beyond 2/2e.

The utility should automate:

- dependency and runtime checks
- device discovery
- left/right tracker association
- pose recording
- stream time alignment
- rotation-only or full 6DoF mount calibration
- quality validation and fallback selection
- VMT configuration
- SteamVR override activation
- profile persistence
- reconnect handling and fail-safe shutdown

The normal user should not need to enter Euler angles, manually edit quaternions, identify OpenVR device indexes, or repeatedly edit `steamvr.vrsettings`.

---

## 3. Naming Decision

Use the repository slug:

```text
lighthouse-touch-bridge
```

Use the product title:

```text
Lighthouse Touch Bridge
```

Reasons:

- **Lighthouse** identifies the authoritative tracking system.
- **Touch** identifies the controller input family used by the initial implementation.
- **Bridge** accurately describes the project: it coordinates existing runtimes and devices rather than replacing them with a new tracking stack.
- The name is not tied to Beyond, Quest 2, or Vive Tracker, leaving room for additional Lighthouse HMDs, newer Touch controllers, and Tundra or other tracked devices.
- The name is descriptive enough to be found through searches such as "Touch controllers with Lighthouse tracking."

Suggested GitHub description:

> Combine Meta Touch controller inputs with Lighthouse-tracked poses in SteamVR, with automatic rotation-only or full 6DoF mount calibration.

Suggested GitHub topics:

```text
steamvr
openvr
mixedvr
meta-quest
oculus-touch
vive-tracker
lighthouse
alvr
vmt
hand-eye-calibration
```

---

## 4. System Concept

```text
Quest 2 + Touch controllers
        |
        | ALVR tracking-reference-only
        | inputs + calibration-time Touch pose
        v
Original SteamVR Touch devices -----------------------------------+
                                                                   |
Physical Lighthouse tracker L ---- pose stream ----+               |
Physical Lighthouse tracker R ---- pose stream ----|               |
                                                    v               |
                                             LTB Coordinator        |
                                             - discovery            |
                                             - association          |
                                             - synchronization      |
                                             - calibration          |
                                             - validation           |
                                             - profile management   |
                                                    |               |
                                                    | X_mount        |
                                                    v               |
                                              VMT virtual devices   |
                                                    |               |
                                                    | TrackingOverrides
                                                    v               v
                                           /user/hand/left and /user/hand/right
```

At runtime:

- Touch button and axis components remain the logical controller inputs.
- VMT devices provide tracker-derived poses corrected by `X_mount`.
- SteamVR TrackingOverrides substitutes those poses for the left and right Touch devices.

---

## 5. Coordinate Model

Define the following frames:

- `Q` — Quest tracking world during calibration.
- `L` — Lighthouse tracking world.
- `C` — the controller pose frame exposed by ALVR/OpenVR before override.
- `T` — the physical Lighthouse tracker frame.
- `Y = T_Q_L` — the unknown transform from Lighthouse world to Quest world.
- `X = T_T_C` — the unknown fixed transform from tracker frame to controller frame.

For synchronized sample `i`:

```text
T_Q_C(i) = Y · T_L_T(i) · X
```

Both `Y` and `X` are unknown. A single static pose cannot separate them.

For two samples `i` and `j`, form relative motions:

```text
A_ij = T_L_T(i)^-1 · T_L_T(j)
B_ij = T_Q_C(i)^-1 · T_Q_C(j)
```

Then:

```text
A_ij · X = X · B_ij
```

The unknown world transform `Y` cancels. Therefore:

- Quest and Lighthouse origins do not need to coincide.
- Their world axes do not need to be pre-aligned.
- OpenVR Space Calibrator is not required for mount calibration.
- Full translation can be solved when both pose streams contain usable position.

The runtime equation is always:

```text
T_L_output(t) = T_L_tracker(t) · X
```

No Quest pose appears in the runtime equation.

---

## 6. Calibration Modes

### 6.1 Rotation-Only Mode

Represent the mount transform as:

```text
X_rotation = [ R_X  0 ]
             [  0   1 ]
```

Runtime output:

```text
p_output(t) = p_tracker(t)
R_output(t) = R_tracker(t) · R_X
```

This mode is valid when:

- Quest position is unavailable, invalid, stale, or heavily occluded.
- The user intentionally accepts the tracker origin as the virtual controller origin.
- The tracker is mounted sufficiently close to the desired controller origin.
- The translation solution is poorly conditioned.

Rotation-only mode is not an error state. It is a supported operating mode and must remain independently testable.

### 6.2 Full 6DoF Mode

Represent the mount transform as:

```text
X_full = [ R_X  t_X ]
         [  0    1  ]
```

Runtime output:

```text
p_output(t) = p_tracker(t) + R_tracker(t) · t_X
R_output(t) = R_tracker(t) · R_X
```

This mode estimates the controller origin relative to the tracker origin. It can reduce the lever-arm error created when the tracker is mounted several centimeters away from the controller pose origin.

Full 6DoF calibration is useful even though Quest position is not used during normal operation. Quest position acts only as a temporary calibration reference.

### 6.3 Auto Mode

Auto is the recommended default.

The solver pipeline must be staged:

1. Estimate stream time offset.
2. Solve rotation from orientation data only.
3. Validate rotation independently.
4. If position samples are valid, test translation observability.
5. Solve translation while holding the accepted rotation fixed.
6. Optionally perform a small joint refinement with separate rotational and positional weights.
7. Compare rotation-only and full 6DoF candidates on held-out samples.
8. Select full 6DoF only if all quality gates pass.
9. Otherwise save and apply the rotation-only result.

This ordering is important. Noisy Quest position must not be allowed to corrupt an otherwise good rotation solution.

---

## 7. Why Optional Translation Can Matter

If the tracker origin and controller origin differ by vector `t_X`, the exact controller position is:

```text
p_controller(t) = p_tracker(t) + R_tracker(t) · t_X
```

Ignoring `t_X` does not change the tracker's own positional measurement, but it changes the point on the rigid body that SteamVR treats as the controller origin.

When a controller rotates around its grip while the tracker is mounted away from that grip, the tracker follows an arc. A rotation-only model reports that arc as hand motion. A full transform reconstructs the intended controller origin.

The practical effect depends on mount geometry:

- If the tracker is close to the desired origin, the difference may be modest.
- If the tracker is mounted high on the tracking ring or on a long bracket, full translation can noticeably improve hand position and weapon/tool alignment.
- Translation calibration does not inherently improve button input or Lighthouse tracking quality.
- Rotation accuracy should remain essentially independent because rotation is solved first and validated separately.

LTB should therefore treat translation as an optional quality improvement, not as a prerequisite for operation.

---

## 8. Pose Acquisition Requirements

During calibration, collect timestamped samples for:

```text
Touch left:       timestamp, orientation, optional position, validity flags
Touch right:      timestamp, orientation, optional position, validity flags
Tracker candidates: timestamp, orientation, position, validity flags, serial
```

Requirements:

- Read the original, non-overridden Touch poses.
- Temporarily disable the active VMT override path before recording.
- Use monotonic host timestamps at the point each sample enters LTB.
- Preserve runtime-provided timestamps or prediction offsets when available.
- Record validity, connectivity, tracking result, and sample age.
- Normalize quaternion sign continuity before differentiation.
- Interpolate orientation with SLERP and position linearly when aligning streams.

Exportable recordings are required from the first development milestone. Calibration must be reproducible offline from a saved recording.

---

## 9. Automatic Tracker-to-Hand Association

Association should not depend on world-space directions because Quest and Lighthouse worlds are unrelated.

Use coordinate-invariant motion signatures:

```text
s(t) = ||omega(t)||
```

where `omega` is angular velocity.

Recommended wizard flow:

1. Ask the user to move only the left controller.
2. Select the tracker with the highest valid angular-motion correlation.
3. Ask the user to move only the right controller.
4. Confirm the remaining tracker and verify correlation.

A simultaneous-motion automatic mode may be added later by solving the assignment matrix between Touch and tracker angular-speed streams.

Reject association when:

- both tracker candidates move similarly
- correlation is weak
- the lag estimate is inconsistent
- a candidate is disconnected or repeatedly invalid

Persist association by tracker serial number, not transient OpenVR device index.

---

## 10. Stream Time Alignment

ALVR Touch poses and Lighthouse tracker poses may have different transport, sampling, prediction, and host scheduling delays.

Initial lag estimate:

```text
lag_0 = argmax_tau corr(
    ||omega_touch(t)||,
    ||omega_tracker(t + tau)||
)
```

Refine lag by minimizing rotational hand-eye residual after interpolation.

Recommended approach:

1. Search a bounded coarse lag range using normalized cross-correlation.
2. Solve a provisional rotation for the strongest lag candidates.
3. Refine lag continuously or at sub-frame resolution by minimizing robust rotational residual.
4. Record the accepted lag and confidence interval.
5. Compare left and right lag estimates; warn if they differ unexpectedly.

Do not estimate translation until time alignment and rotation have passed validation. Timing error can masquerade as a mount translation or rotational error during fast motion.

---

## 11. Rotation Solver

From relative motions:

```text
R_A · R_X = R_X · R_B
```

Use many motion pairs with diverse axes and sufficient angular separation.

Implementation options:

- quaternion linear solve followed by unit normalization
- SVD/Kronecker-product solve
- nonlinear robust refinement on `SO(3)`

Recommended pipeline:

1. Build candidate motion pairs from synchronized samples.
2. Reject pairs with very small rotation.
3. Avoid using every possible pair; select a balanced set across the recording.
4. Obtain a closed-form initial estimate.
5. Refine with a robust loss over geodesic rotation residuals.
6. Evaluate on held-out samples.

The user gesture must include rotations about at least two non-parallel axes. The wizard should display excitation coverage rather than merely counting seconds.

---

## 12. Translation Solver

After accepting `R_X`, solve the translation part of:

```text
A · X = X · B
```

For each relative motion pair:

```text
(R_A - I) · t_X = R_X · t_B - t_A
```

Stack equations from many valid pairs and solve with weighted least squares or a robust estimator.

Translation quality requirements:

- Quest and tracker positions must be valid for the selected interval.
- Relative rotations must excite multiple axes.
- The stacked system must have acceptable rank and condition number.
- The estimated translation magnitude must be physically plausible.
- Residuals must remain stable across data subsets.
- Held-out positional error must improve over `t_X = 0` by a configured margin.

Do not use a full 6DoF result merely because a numerical solution exists. Observability and validation determine whether it is accepted.

---

## 13. Optional Joint Refinement

After separate rotation and translation solves, optionally refine:

```text
X = [R_X, t_X]
time lag = delta_t
```

by minimizing a robust objective over synchronized pose pairs.

Use separate scale factors for:

- angular residual in radians
- positional residual in meters

The optimizer must start from the staged solution and remain bounded. If refinement worsens held-out rotation or produces an implausible translation, discard it and keep the staged solution.

Joint refinement is not required for the first proof of concept. The staged closed-form/least-squares path is sufficient for the initial MVP.

---

## 14. Model Selection and Quality Gates

Produce two candidates whenever position is available:

```text
Candidate A: X = [R_X, 0]
Candidate B: X = [R_X, t_X]
```

Evaluate both on samples not used to solve them.

Suggested metrics:

- rotation RMS and percentile error
- position RMS and percentile error
- temporal lag confidence
- motion-axis coverage
- translation-system condition number
- inlier ratio
- estimated translation magnitude
- consistency across split recordings

Auto mode selects full 6DoF only when:

- rotation is already accepted
- position validity is high enough
- translation is observable
- translation is plausible
- validation error improves on held-out data
- the result is stable across resampling or split tests

Store the reason for mode selection in the profile and logs.

Example:

```json
{
  "selected_mode": "full_6dof",
  "selection_reason": "translation observable; held-out position RMS improved",
  "rotation_rms_deg": 1.2,
  "position_rms_mm": 8.4,
  "lag_ms": 11.5
}
```

Numeric thresholds should be configurable and tuned from recorded hardware data rather than treated as permanent constants in the initial specification.

---

## 15. VMT Integration

Use one stable VMT virtual device per hand.

Conceptually configure:

```text
VMT left:
  follow device = physical tracker left serial
  local position = t_X_left
  local rotation = R_X_left

VMT right:
  follow device = physical tracker right serial
  local position = t_X_right
  local rotation = R_X_right
```

For rotation-only profiles:

```text
local position = [0, 0, 0]
```

For full 6DoF profiles:

```text
local position = calibrated t_X
```

LTB must verify the transform convention with an integration test. If VMT expects the inverse or a different quaternion ordering, conversion must occur in one isolated adapter layer and be covered by tests.

The calibration library must use an explicit internal convention and must not spread VMT-specific axis or serialization rules through the solver.

---

## 16. SteamVR TrackingOverrides

Use stable VMT device paths as pose sources for the original Touch semantic paths.

Illustrative configuration:

```json
{
  "steamvr": {
    "activateMultipleDrivers": true
  },
  "TrackingOverrides": {
    "/devices/vmt/VMT_1": "/user/hand/left",
    "/devices/vmt/VMT_2": "/user/hand/right"
  }
}
```

The coordinator must:

- discover actual device paths rather than assume examples
- back up `steamvr.vrsettings`
- parse and merge JSON safely
- preserve unrelated user settings
- write atomically
- validate the result after writing
- provide rollback
- never leave an override active with a stale or disconnected pose source

Initial setup may require one SteamVR restart. Routine calibration and profile switching should avoid a restart when VMT device activation can release and reacquire the override safely.

---

## 17. ALVR Role

ALVR should run in a mode that does not replace the active Lighthouse HMD.

Required behavior:

- expose Quest 2 Touch controllers to SteamVR
- keep their input components active
- expose their original poses during calibration
- avoid registering Quest 2 as the active SteamVR display HMD

After calibration, Touch position and orientation may be ignored by LTB. The Quest headset still needs to remain connected sufficiently for the controllers and their inputs to stay alive.

LTB should treat ALVR as an external dependency for the first release rather than maintaining an ALVR fork.

---

## 18. Application State Machine

Suggested states:

```text
Stopped
  -> DependencyCheck
  -> WaitingForSteamVR
  -> WaitingForDevices
  -> Ready
  -> OverrideRelease
  -> Recording
  -> Association
  -> TimeAlignment
  -> RotationSolve
  -> TranslationAttempt
  -> Validation
  -> ApplyProfile
  -> Active
```

Failure transitions:

```text
Any calibration state -> Ready with diagnostic
Active + tracker lost -> SafeDisable
Active + Touch input device lost -> SafeDisable or degraded warning
Active + VMT unavailable -> SafeDisable
SteamVR stopped -> Stopped
```

The application must distinguish:

- no position available — normal rotation-only fallback
- poor translation observability — normal rotation-only fallback
- bad rotation calibration — calibration failure
- lost tracker — runtime safety failure

---

## 19. Profile Format

Profiles are keyed by controller side, tracker serial, controller identity, and mount identity where available.

Example:

```json
{
  "schema_version": 1,
  "profile_name": "Quest2 Touch + Vive Tracker mount A",
  "hand": "left",
  "controller_runtime": "ALVR",
  "controller_model": "Quest 2 Touch",
  "controller_serial": "optional-runtime-identifier",
  "tracker_serial": "LHR-XXXXXXXX",
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
  "created_utc": "2026-07-17T00:00:00Z"
}
```

A rotation-only profile stores zero translation and records why translation was not selected.

---

## 20. User Experience

### First run

1. Detect SteamVR, ALVR, VMT, active HMD, Touch devices, and tracker candidates.
2. Explain any missing dependency with a direct remediation message.
3. Release existing hand overrides.
4. Ask the user to keep Quest cameras able to observe the Touch controllers if full 6DoF is desired.
5. Ask the user to move the left controller through pitch, yaw, roll, and moderate translation.
6. Repeat for the right controller.
7. Show live motion-coverage and tracking-validity indicators.
8. Solve and validate.
9. Display the selected mode per hand:
   - Full 6DoF
   - Rotation-only fallback
10. Apply VMT transforms and overrides.
11. Save profiles.

### Later runs

1. Detect tracker serials.
2. Load matching profiles.
3. Verify devices and VMT.
4. Apply transforms.
5. Activate overrides.
6. Monitor health.

### Recalibration triggers

- user explicitly requests recalibration
- tracker attached to a different hand
- mount physically moved
- validation check exceeds threshold
- controller model or emulation mode changed
- stored transform convention or schema changed

---

## 21. MVP Scope

### Version 0.1 must include

- Windows desktop or tray application
- C#/.NET calibration core and coordinator
- SteamVR/OpenVR device discovery
- original Touch and tracker pose recording
- serial-based tracker association
- stream-lag estimation
- rotation-only solver
- optional translation solver
- Auto model selection with reliable fallback
- validation and quality reporting
- VMT transform application
- safe TrackingOverrides management
- profile persistence
- runtime watchdog
- structured logs
- exportable and replayable recordings
- synthetic calibration tests

### Version 0.1 should exclude

- custom SteamVR controller driver
- ALVR fork
- automatic installation of third-party software
- VR dashboard UI
- support for every controller family
- cloud accounts or telemetry
- automatic firmware management
- general Quest-to-Lighthouse playspace calibration

Initial supported hardware path:

```text
Beyond 2/2e
+ Quest 2
+ Quest 2 Touch
+ two Vive Trackers
+ Lighthouse base stations
+ ALVR
+ VMT
```

The internal interfaces should still use generic names such as `InputControllerPoseSource` and `TrackedPoseSource` to avoid unnecessary hardware lock-in.

---

## 22. Suggested Technology Stack

Recommended implementation:

- **C# / .NET 8 or later**
- Windows desktop UI using WinUI 3, WPF, or Avalonia
- OpenVR interop for device enumeration, input state, and pose sampling
- OSC or the supported VMT control interface for VMT configuration
- a numerical library supporting matrices, quaternions, SVD, and least squares
- JSON configuration with schema versioning
- structured logging

Recommended architecture:

```text
/src
  /Ltb.App
  /Ltb.Core
  /Ltb.OpenVr
  /Ltb.Alvr
  /Ltb.Vmt
  /Ltb.Calibration
  /Ltb.Configuration
/tests
  /Ltb.Calibration.Tests
  /Ltb.Configuration.Tests
  /Ltb.Integration.Tests
/tools
  /Ltb.RecordingInspector
  /Ltb.SyntheticData
/docs
  architecture.md
  calibration.md
  setup.md
  troubleshooting.md
```

The calibration library should have no UI or SteamVR dependency. It should operate on timestamped pose arrays and support deterministic offline replay.

---

## 23. Test Plan

### 23.1 Synthetic tests

Generate streams from known:

- tracker-to-controller rotation
- tracker-to-controller translation
- Quest-to-Lighthouse world transform
- fixed time lag

Add:

- rotational noise
- positional noise
- dropped samples
- variable sample rates
- timestamp jitter
- quaternion sign flips
- outliers
- tracking discontinuities
- one-axis degenerate motion
- translation-degenerate motion
- partial position availability

Verify:

- rotation recovery
- translation recovery when observable
- correct fallback when not observable
- lag recovery
- left/right association
- deterministic model selection
- robust rejection of outliers

### 23.2 Recorded-stream tests

Capture hardware recordings for:

- arbitrary tracker mounting orientations
- small and large mount translations
- slow and fast motion
- partial Quest controller occlusion
- loss of Quest position while orientation remains valid
- tracker occlusion
- ALVR reconnect
- SteamVR restart
- swapped trackers
- physical remount

Keep anonymized example recordings in a separate test-data release or repository if binary size becomes large.

### 23.3 Integration tests

Verify:

- the VMT transform direction and quaternion order
- zero translation preserves the tracker origin
- full translation moves the output origin correctly under rotation
- button and axis inputs remain sourced from Touch
- override release reveals the original Touch pose
- override activation uses the intended VMT device
- tracker loss disables the affected override
- unrelated SteamVR settings survive configuration updates

### 23.4 Hardware acceptance tests

- static controller alignment at varied orientations
- rapid pitch, yaw, and roll
- in-place wrist rotation with tracker mounted away from grip
- aiming and tool alignment in multiple SteamVR applications
- controller input coverage
- repeated startup without recalibration
- profile reuse after reboot
- safe behavior after tracker battery loss

---

## 24. Milestones

### Milestone 0 — Offline calibration proof

- define coordinate conventions
- generate synthetic paired pose streams
- solve rotation-only `AX = XB`
- solve translation after rotation
- validate degeneracy detection
- produce a command-line report

### Milestone 1 — Live recorder

- enumerate SteamVR devices
- record Touch and tracker streams
- export recordings
- replay through the offline solver
- estimate stream lag

### Milestone 2 — One-hand live bridge

- configure one VMT device
- apply one transform
- activate one hand override
- verify inputs and pose in SteamVR

### Milestone 3 — Two-hand calibration wizard

- tracker association
- guided motion
- Auto model selection
- quality report
- profile persistence

### Milestone 4 — Reliable daily use

- startup sequencing
- reconnect handling
- watchdog
- rollback
- installer and documentation

### Milestone 5 — Generalization

- newer Meta Touch controllers
- Tundra Tracker and generic Lighthouse devices
- additional Lighthouse HMDs
- optional custom driver investigation only if VMT/override limitations justify it

---

## 25. Definition of Done for Version 0.1

Version 0.1 is complete when a user can:

1. Run Beyond 2/2e as the active SteamVR HMD.
2. Connect Quest 2 through ALVR tracking-reference-only.
3. See both Quest 2 Touch input devices and two Lighthouse trackers.
4. Attach one tracker rigidly to each controller in arbitrary orientation.
5. Start the LTB wizard.
6. Perform the requested multi-axis motion.
7. Receive automatic tracker-to-hand association.
8. Receive a validated rotation solution for each hand.
9. Receive a full translation solution when Quest position and motion quality support it.
10. Fall back cleanly to rotation-only when they do not.
11. Activate VMT and TrackingOverrides without manually entering transforms.
12. Use Touch buttons and axes while Lighthouse devices provide the runtime pose.
13. Restart later and restore the correct serial-based profiles.
14. Lose a tracker without leaving a permanently frozen virtual hand.
15. Export logs and recordings sufficient to reproduce a calibration failure.

---

## 26. Project Assessment

This is an appropriate small open-source project.

The mathematical core is compact, but the practical value is in reliable orchestration:

- mixed-driver SteamVR startup
- access to original controller poses
- time alignment across runtimes
- robust hand-eye calibration
- automatic model selection
- safe VMT and override sequencing
- serial-based persistence
- failure recovery
- usable diagnostics

For one fixed personal mount, a manually tuned VMT transform could be enough. For repeatable setup, arbitrary mounts, automatic calibration, or use by other people, a dedicated repository is justified.

The project should begin as a coordinator and calibration utility, not a custom SteamVR driver. A new driver should be considered only if empirical testing shows that VMT or TrackingOverrides prevents reliable input/pose composition.

---

## 27. README Opening Draft

```markdown
# Lighthouse Touch Bridge

Meta Touch inputs. Lighthouse tracking. One SteamVR controller.

Lighthouse Touch Bridge is a Windows utility that combines Meta Touch controller inputs with poses from Lighthouse-tracked devices. It is designed for mixed-VR setups where a Lighthouse HMD such as Bigscreen Beyond is used with Quest Touch controllers whose runtime position and orientation are replaced by mounted Vive Trackers.

LTB automatically associates controllers and trackers, aligns their pose streams in time, and calibrates the fixed mount transform. It supports rotation-only calibration when Quest position is unavailable and full 6DoF calibration when reliable position data is present. At runtime, only the Lighthouse tracker pose is used; the Quest system remains connected to provide controller inputs.
```
