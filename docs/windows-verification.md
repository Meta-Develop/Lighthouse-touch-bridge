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

## Milestone 2 one-hand live bridge

Milestone 2 adds deterministic Linux coverage for OSC encoding, the
tracker-local VMT transform adapter, fixture-based SteamVR settings updates and
recovery, one-hand activation with fake backends, verification-contract
enforcement, and SafeDisable after simulated health loss. These tests prove
the coordinator and file-level contracts only. They are not evidence that a
real VMT device appears, that SteamVR applies the override, or that a real
Touch controller remains the input source while VMT supplies its pose.

Use the command and profile contract in [setup.md](setup.md). The transform,
adapter, settings, and health decisions are recorded in
[architecture.md](architecture.md), and the acceptance intent comes from
specification sections [15, 16, 18, and 24](specification.md). VMT command and
override behavior should be compared with the
[official VMT API](https://gpsnmeajp.github.io/VirtualMotionTrackerDocument/api/);
OpenVR registered-device properties and tracker classes should be compared with
Valve's [driver API documentation](https://github.com/ValveSoftware/openvr/blob/master/docs/Driver_API_Documentation.md).

### Milestone 2 evidence requirements

For every checked item below, retain a dated evidence record containing:

- Windows, SteamVR, VMT, ALVR, .NET, tracker firmware, and LTB commit versions;
- the exact LTB command with local paths and stable hardware identifiers
  redacted consistently;
- a redacted profile or its hash, including the tested hand, selected mode,
  translation, and quaternion;
- redacted `devices` output and the relevant SteamVR System Report excerpt
  before activation, while active, and after SafeDisable when applicable;
- a semantic before/after `steamvr.vrsettings` diff and hashes of any backups,
  retained outside the repository rather than committing settings or backups;
- timestamped LTB output and the VMT/SteamVR diagnostic excerpt needed to
  establish the tested transition; and
- for input/pose provenance checks, a screen recording or synchronized runtime
  capture that shows the physical action and observed input or pose change.

Redaction must preserve stable token correspondence within one record. For
example, the same tracker can be `TRACKER-A` throughout while its real serial
and owner-local path remain absent. Record failures as evidence too; do not
check an item merely because the Linux fake for that transition passes.

### Device, transform, and override acceptance

- [ ] **Register a fresh slot, then discover the real VMT source.** With VMT
  installed and enabled, close VMT Manager so LTB can own response port
  `39571`, start a fresh SteamVR session, and record whether the requested slot
  is absent from `devices`. Confirm LTB attempts both selected-slot VMT
  deactivation and stale exact mapping release before any SteamVR enumeration
  or device-selection gate, reports either failure, and does not attempt a new
  activation unless both cleanup surfaces succeed. Confirm it then enumerates
  and passes tracker, Touch, and heartbeat gates, sends the enabled Joint
  configuration with device mode `1` (`Tracker`), and only then discovers one
  connected `GenericTracker`. Confirm its actual source path comes from the
  OpenVR registered-device property normalized to
  `/devices/<driver>/<device>` and is the path used for activation and cleanup.
  Record the redacted raw property,
  normalized path, stable identity, class, connectivity, and transient index.
  First register a separate test slot in a non-Tracker mode, confirm changing
  the later command does not reclassify it in that SteamVR process, then restart
  SteamVR and confirm LTB's first mode-1 registration yields `GenericTracker`.
  Also confirm discovery succeeds if its transient index changes.

- [ ] **Validate `/VMT/Joint/Driver` transform direction and serialization.**
  Use a safe, measured mount or bench sequence with independently recognizable
  positive translation and rotations about multiple axes. Confirm the wire
  values are right-handed meters and normalized quaternion `XYZW`, represent
  `T_T_C`, and produce `T_L_tracker * T_T_C`. Verify both translation lever-arm
  motion and orientation. Confirm `/VMT/Set/AutoPoseUpdate(1)` is sent before
  the enabled Joint command and that the result is tracker-local rather than a
  room-space rotation. Repeat once with zero translation for a rotation-only
  profile.

- [ ] **Make the VMT device appear and apply the one-hand override.** Start from
  no mapping for the selected semantic hand. Run the bridge and confirm the
  chosen VMT slot becomes connected before the settings mapping is enabled.
  Confirm LTB reads that VMT output through OpenVR and requires a connected,
  `RunningOk`, orientation-valid, position-valid, and tracking-valid pose before
  writing the mapping. Confirm the measured VMT output agrees with
  `T_L_tracker * T_T_C` within the implemented sample-skew, position-error, and
  rotation-error safety bounds before enable and throughout monitoring: `0.05`
  seconds, `0.15` meters, and `pi/9` radians (20 degrees), respectively. Treat
  these as fail-safe mismatch limits rather than calibration-quality targets.
  Confirm `activateMultipleDrivers` is true, the discovered source maps to the
  intended `/user/hand/left` or `/user/hand/right`, unrelated settings and
  mappings are unchanged, and SteamVR reports the hand pose following the
  mounted tracker through the calibrated Joint transform. Record
  `effective_monitor_rate_hz` and confirm it is at least the requested rate and
  fast enough for both configured freshness bounds.

- [ ] **Prove Touch input provenance while VMT supplies pose.** While the
  override is active, hold the tracker and Touch controller in a configuration
  that distinguishes their pose sources. Exercise buttons, trigger, grip,
  stick axes, and capacitive states where exposed. Confirm the semantic hand's
  pose follows tracker plus `T_T_C`, while every exercised input continues to
  originate from the selected Touch controller. Record the redacted Touch
  identity, VMT source path, application input observations, and pose
  observations; an active mapping alone is insufficient evidence.

- [ ] **Release the override and reveal the original Touch pose.** Press Ctrl+C
  from a healthy active run. Confirm LTB disables the VMT slot, removes only
  its exact source-to-hand mapping, reports zero SafeDisable failures, and
  leaves unrelated settings intact. With Touch and tracker deliberately
  distinguishable, confirm the hand pose returns to the original ALVR Touch
  pose while Touch inputs remain usable.

### SafeDisable and settings acceptance

- [ ] **SafeDisable on physical-tracker loss or invalid pose.** In separate
  runs, safely occlude tracking, power off or disconnect the tracker, and
  induce each available non-`RunningOk` or invalid-pose state. If the backend
  exposes sample age, also cross the configured `--stale-after` threshold and record
  the measured age. If age remains unavailable from synchronous OpenVR, record
  that limitation rather than claiming an age test. Confirm the effective
  monitor interval is no longer than half the configured stale threshold. Each
  failure must produce
  health exit code `3` only after both VMT deactivation and exact mapping
  release succeed; no stale override may remain active.

- [ ] **SafeDisable on VMT output or driver loss.** During separate active
  runs, induce invalid, non-`RunningOk`, disconnected, and reported-stale VMT
  output poses; stop the VMT driver or its heartbeat path; and make the active
  VMT device disappear or change identity. If synchronous OpenVR exposes no
  pose age, record that limitation and do not claim a measured VMT-age test.
  Confirm each available output-pose failure, stale `/VMT/Out/Alive`, VMT
  disconnect, disappearance, and identity change enters SafeDisable. Verify
  both cleanup steps are attempted and the hand is not left mapped to a stale
  virtual source.

- [ ] **SafeDisable on Touch loss.** Disconnect or power down only the selected
  Touch controller while the tracker and VMT remain healthy. Confirm LTB treats
  loss of the input device as a health failure, disables the virtual source,
  and releases the exact hand mapping instead of leaving pose without the
  intended inputs.

- [ ] **Expose cleanup failures without skipping the second cleanup step.** In
  controlled tests, deny one VMT deactivation and separately deny settings
  release during startup and SafeDisable. Confirm a deactivation failure does
  not prevent an attempted exact mapping release, every failure is reported,
  startup cleanup failure blocks new activation, and the command returns
  cleanup exit code `4`. On Ctrl+C cleanup failure, confirm output does not say
  the bridge was safely disabled. Restore the environment manually before the
  next test and confirm no unreviewed override remains.

- [ ] **Recover from an abrupt, unmanaged termination.** Use a controlled
  non-worn setup with the tracked area clear. After reaching `state: active`,
  terminate only the LTB process without Ctrl+C so its managed cleanup cannot
  run; do not induce a real OS crash or power loss. Confirm the terminated
  process makes no safe-disable claim. Before recovery, separately inspect and
  record whether the VMT slot remains enabled and whether the persistent exact
  `TrackingOverrides` mapping remains. Start the same command again and confirm
  it attempts both cleanup surfaces before any SteamVR enumeration or device
  selection, clears the residual state, and blocks activation if either cleanup
  fails. Finally, with LTB stopped, exercise the documented reviewed settings
  recovery as needed, restart SteamVR, and confirm the old runtime device and
  stale mapping no longer control the hand. Record that console destruction,
  OS crash, and power loss have the same unmanaged-cleanup limitation even
  though those destructive cases were not induced.

- [ ] **Verify backup, atomic replacement, rollback, ACL, and lock behavior.**
  Begin with unrelated typed settings and at least one unrelated override.
  Confirm each actual LTB change creates a unique, byte-exact sibling
  `.ltb-backup`, same-directory staging is flushed before replacement, and
  post-write JSON and intended mapping validation pass. Exercise a denied file
  ACL, denied directory create/rename permission, an LTB sibling-lock
  contender, a SteamVR or external-writer race, a malformed settings file, and
  a forced post-write validation failure. Confirm bounded diagnostics, no
  partial JSON, automatic restoration only when LTB still owns its write, and
  preservation of a later external winner. Recover one reviewed sibling backup
  and confirm the replaced content receives its own undo backup.

### Restart and profile reuse acceptance

- [ ] **Record the first required SteamVR restart.** Starting from a clean VMT
  installation or newly enabled add-on, document whether VMT requests a
  restart and remember that device mode is honored only on first registration
  after SteamVR starts. Confirm LTB sends mode `1` (`Tracker`) and the slot
  becomes `GenericTracker`. If it was first registered in another mode, restart
  SteamVR and confirm LTB's next mode-1 command supplies the first registration.
  After the restart, confirm the requested VMT slot is discoverable, the
  one-hand mapping takes effect, Touch input remains live, and SafeDisable
  releases the mapping. Record the exact boundary at which the first restart
  was necessary rather than attributing it to routine profile activation.

- [ ] **Reuse the profile without routine manual edits.** After one successful
  run, start and stop the same profile again without restarting SteamVR.
  Confirm discovery, activation, monitoring, and release all succeed without a
  manual settings edit. Then restart SteamVR and reuse the profile again;
  rediscover the device path rather than relying on its prior transient index,
  confirm any intended persistent VMT registration behavior, and verify that
  no stale prior mapping or duplicate source owns the hand. Record whether the
  normalized source path remains stable and investigate any change before
  accepting profile reuse.
