# Windows Runtime Verification

Milestone 1 has deterministic Linux tests for the portable recording, replay,
device-enumeration adapter, and lag-estimation contracts. Those tests do not
exercise a real SteamVR runtime, ALVR transport, OpenVR timing, USB reconnect
behavior, or live device-index assignment. This file is the running checklist
for deferred Windows and live-runtime acceptance, plus offline cross-platform
parity checks. Each item states the environment and dependencies it needs.

Keep captured evidence free of credentials and owner-local absolute paths. Do
not commit real device serials, SteamVR configuration backups, or hardware
recordings. Redact stable identifiers in reports and keep raw captures in an
ignored local directory.

## Status and test setup

- `[ ]` means deferred or not yet evidenced in the environment stated by that
  item.
- `[x]` means the check was completed and its dated, redacted evidence was
  reviewed.
- Automated Linux checks remain part of `dotnet build` and `dotnet test`; they
  must not be checked off as substitutes for the live-runtime or hardware
  evidence required by an item.

## Automated Linux scope

The automated suite covers deterministic fake-device enumeration and pose
sources, recording schema round trips and version rejection, synthetic lag
recovery, offline replay through the Milestone 0 solver, native deployment
hashes, and the existing calibration regressions. The inspector remains a
separate synthetic CLI acceptance check; its textual summary does not currently
have an automated regression assertion. Passing the automated checks is the
Linux acceptance gate for portable behavior. Each unchecked item below needs
only its stated environment. Offline inspector and replay checks can run
without SteamVR, ALVR, or connected hardware; checks that explicitly exercise
live acquisition, runtime integration, or devices remain deferred to the
corresponding Windows/SteamVR/ALVR/hardware setup.

## Supported native deployment

Valve's official `bin/win64/openvr_api.dll` is vendored from OpenVR SDK 2.15.6
commit `0924064316de3effbcd1acf1e309182a2deb1c05` under
`src/Ltb.OpenVr/runtimes/win-x64/native/`. Produce the supported
framework-dependent Windows x64 layout from the repository root with:

```powershell
dotnet publish src/Ltb.App/Ltb.App.csproj -c Release -r win-x64 --self-contained false
```

The `Ltb.OpenVr` project copies `openvr_api.dll` into the application publish
root beside `Ltb.App.exe` and `Ltb.OpenVr.Interop.dll`. The generated binding
imports `openvr_api`, so .NET resolves the app-local `openvr_api.dll` without a
manual SDK copy or `PATH` change. The Valve BSD notice is copied to
`licenses/Valve.OpenVR.LICENSE.txt`. The expected native DLL SHA-256 is
`bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a`.
This deployment supplies the OpenVR client library; SteamVR must still be
installed and running for live initialization.

For a live-runtime or hardware verification session, record the applicable
details below. For an offline inspector or replay session, record only the OS,
.NET version, LTB commit, exact command, and redacted input-fixture identity.

- Windows version and architecture;
- SteamVR and ALVR versions, plus the OpenVR binding or backend version;
- active HMD and controller emulation mode;
- tracker models and firmware versions, with serials redacted;
- LTB commit and exact command line; and
- whether any `TrackingOverrides` entry was active before setup.

## Native runtime lifecycle

- [ ] **Load and initialize the pinned binding.** Publish with the supported
  `win-x64` command above and confirm that the output-root `openvr_api.dll`
  matches the documented hash before launch. On the supported 64-bit Windows
  process, confirm that this app-local library resolves, the application
  initializes without becoming the active HMD, and shutdown releases the
  OpenVR context cleanly. Repeat the initialize/shutdown cycle and retain the
  reported interface and SDK versions.

- [ ] **Diagnose an unavailable or incompatible runtime.** Run with SteamVR
  stopped, with the native library unavailable, and with an interface-version
  failure if it can be induced safely. Confirm that each case returns a bounded
  actionable diagnostic and leaves no partially initialized context.

## Device discovery and identity

- [ ] **Enumerate a complete mixed-VR device set.** Start SteamVR with the
  Lighthouse HMD active, ALVR exposing both Touch controllers, and at least two
  Lighthouse trackers connected. Confirm that LTB reports each relevant
  device once and maps runtime class and role correctly. Retain a redacted
  enumeration transcript and compare it with the SteamVR device view.

- [ ] **Reject or explain incomplete runtime state.** Repeat enumeration with
  SteamVR stopped, ALVR disconnected, one controller absent, and one tracker
  powered off. Confirm that each state produces a bounded diagnostic rather
  than a crash, stale device, or misclassified replacement.

- [ ] **Preserve tracker identity across reconnect and index churn.** Record
  the redacted tracker identities and assigned OpenVR indexes, power-cycle the
  trackers in the opposite order, and restart SteamVR. Confirm that serials
  remain stable, indexes may change without affecting identity, and reconnect
  neither duplicates nor swaps the tracker streams.

- [ ] **Distinguish left and right input controllers.** Confirm that controller
  role and identity remain correct through ALVR reconnect and SteamVR restart.
  Record the runtime properties used for the decision and any ambiguous state.

## Original Touch poses and override safety

- [ ] **Require override release before recording.** Begin with a hand
  `TrackingOverrides` mapping active. Confirm that LTB refuses to treat the
  overridden pose as the original Touch stream and reports that release is a
  prerequisite. For Milestone 1, release the mapping through the existing
  external setup; the recorder must not write `TrackingOverrides`. Confirm the
  original pose is visible before capture begins, and preserve a redacted
  before/after settings snapshot outside the repository.

- [ ] **Read the original, non-overridden Touch pose.** With the override
  released, move the Touch controller and its mounted tracker differently
  enough to distinguish their reported poses. Confirm that the Touch stream is
  the ALVR-provided original pose rather than the tracker, VMT, or previously
  overridden pose.

- [ ] **Handle an unmet override-release prerequisite safely.** Leave the
  override active or disconnect its source. Confirm that the operator is told
  not to proceed, no capture is treated as original-pose evidence, and the
  Milestone 1 recorder leaves SteamVR settings unchanged.

## Pose semantics and timing

- [ ] **Validate coordinate and transform conversion.** At several known
  static orientations and translations, compare adapter output with the
  runtime pose. Confirm right-handed axes, meters, normalized quaternion `XYZW`
  ordering, tracker-to-controller transform direction, and no hidden inversion
  or transpose.

- [ ] **Validate host timestamp placement and monotonicity.** Confirm that the
  host timestamp is taken when each sample enters LTB, increases monotonically
  within every stream, and remains in seconds. Exercise a long capture and a
  system scheduling stall; no wall-clock adjustment may reorder samples.

- [ ] **Characterize runtime timing semantics.** Determine which runtime pose
  timestamp or prediction-offset value is available for each device type,
  where it is measured relative to the host sample time, and whether its sign
  matches the documented interpretation. Confirm that the value is preserved
  in export rather than silently folded into the host timestamp.

- [ ] **Measure sampling rate and jitter.** For each Touch and tracker stream,
  collect at least one sustained capture and report sample count, duration,
  median rate, inter-sample median, high percentile, maximum interval, duplicate
  timestamps, and backward timestamps. Repeat during moderate CPU load.

- [ ] **Validate sample age.** Compare the recorded sample-age value with the
  runtime timing observation over steady motion, rest, and induced scheduling
  delay. Confirm units, sign, and behavior for unavailable runtime timing.

- [ ] **Map validity, connectivity, and tracking result independently.** For
  each runtime tracking-result state that can be induced safely, confirm the
  exported orientation-valid, position-valid, tracking-valid, connected, and
  tracking-result fields. Test tracker occlusion, controller camera occlusion,
  standby, power-off, and recovery; a stale transform must not become a valid
  sample merely because the device remains connected.

- [ ] **Preserve quaternion continuity for motion analysis.** Exercise motion
  that crosses a quaternion sign boundary. Confirm that the recorded pose
  remains equivalent and lag analysis has no artificial angular-speed spike.

## Recording and export

- [ ] **Complete a sustained mixed-stream recording.** Record both hands and
  both tracker candidates for at least ten minutes with slow motion, fast
  multi-axis motion, rest intervals, temporary occlusion, and one device
  reconnect. Confirm bounded memory growth, clean stop, final flush, and a
  readable versioned export.

- [ ] **Verify exported field fidelity.** Inspect a redacted copy locally and
  confirm schema version, stream identity, timestamps, transforms, validity,
  connectivity, tracking results, timing metadata, and sample order match the
  live observations. Confirm that non-finite values and unsupported schema
  versions are rejected with useful diagnostics.

- [ ] **Verify interrupted-capture behavior.** Stop the application normally,
  close the console during capture, and stop SteamVR during capture. Document
  whether each output is finalized or explicitly rejected as incomplete; no
  corrupt partial file may be mistaken for a complete replay fixture.

- [ ] **Inspect the capture offline.** On a machine without SteamVR running,
  use `Ltb.RecordingInspector` to report duration, per-stream sample counts and
  rates, validity ratios, motion coverage, and lag. Confirm the report does not
  require live enumeration; redact identifiers before retaining the report as
  evidence.

## Replay and lag acceptance

- [ ] **Establish offline replay equivalence.** Replay the same exported file
  twice with identical calibration options and compare the complete
  calibration result and report. Repeat on Linux and Windows. The selected
  samples, lag, mount transform, quality metrics, and mode-selection reason
  must be identical within the format's numeric round-trip precision.

- [ ] **Compare live and exported paths.** For one completed capture, retain
  the in-memory/live result and replay the exported recording. Confirm that
  both use the same alignment and solver entry points and produce equivalent
  lag and calibration results.

- [ ] **Recover a hardware-observed lag.** Capture repeated sharp but safe
  multi-axis motions, inspect the angular-speed correlation peak, and confirm
  that the accepted lag is stable across recording subsets. Record the search
  bounds, resolution, peak score, runner-up separation, accepted lag, and any
  refinement result.

- [ ] **Check left/right lag consistency.** Capture both hands under the same
  ALVR and SteamVR session, estimate lag independently, and compare the results
  and confidence. Repeat after ALVR reconnect and under moderate CPU load.
  Investigate a persistent difference rather than averaging it away; record
  whether the discrepancy follows one hand, one tracker, or the session.

- [ ] **Exercise weak and ambiguous motion.** Record rest, single-axis motion,
  repeated periodic motion, dropped samples, and partial validity. Confirm that
  lag confidence is reduced or the estimate is rejected instead of reporting
  an apparently precise unsupported offset.

## Evidence record

For each checked item, append a short dated entry containing the test ID or
heading, environment summary, result, redacted evidence location, and any
follow-up issue. If a runtime or binding update changes enumeration, pose, or
timestamp semantics, reopen the affected checks.
