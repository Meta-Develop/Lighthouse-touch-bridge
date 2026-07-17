# Windows Runtime Verification

The automated suite has deterministic Linux tests for calibration, recording,
replay, device enumeration, one-hand bridge safety, the fakeable
reliable-daily-use coordinator, rollback, and structured-event contracts. Those
tests do not exercise a real SteamVR runtime, ALVR transport, OpenVR timing,
Windows ACLs, USB reconnect behavior, code signing, or live device-index
assignment. This file is the consolidated checklist for deferred Windows and
hardware acceptance, including specification section 23.4. Each item states
the environment and dependencies it needs.

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
recovery, offline replay, native deployment hashes, startup and failure
transitions, stable-serial reacquisition, SafeDisable, transactional apply
rollback, structured events, and the existing calibration regressions. The inspector remains a
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
`src/Ltb.OpenVr/runtimes/win-x64/native/`. Produce the supported self-contained
Windows x64 layout from the repository root with:

```bash
dotnet publish src/Ltb.App/Ltb.App.csproj -p:PublishProfile=win-x64
```

The publish profile is Release, `net8.0`, `win-x64`, self-contained, pins
`RuntimeFrameworkVersion` to `8.0.28`, and is non-single-file, untrimmed, and
not ReadyToRun. It writes generated output to
`artifacts/publish/win-x64/`. Produce the portable release ZIP, version
manifest, and checksum with:

```bash
bash build/package-win-x64.sh 0.1.0
```

The packaging script requires .NET 8, Git, Bash, and Python 3 on the build
machine. End users need none of those tools or a separate .NET installation;
they run `Ltb.App.exe` from the extracted self-contained package. The script
refuses to overwrite an existing same-version archive.

The `Ltb.OpenVr` project copies `openvr_api.dll` into the application publish
root beside `Ltb.App.exe` and `Ltb.OpenVr.Interop.dll`. The generated binding
imports `openvr_api`, so .NET resolves the app-local `openvr_api.dll` without a
manual SDK copy or `PATH` change. The Valve BSD notice is copied to
`licenses/Valve.OpenVR.LICENSE.txt`. The expected native DLL SHA-256 is
`bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a`.
This deployment supplies the OpenVR client library; SteamVR must still be
installed and running for live initialization.

The portable ZIP must contain `release-manifest.txt`, `LICENSE.txt`, the full
packaged documentation set including `specification.md`, and the publish layout. Its adjacent
`.sha256` file covers the complete ZIP. The package is unsigned; signing,
SmartScreen, native launch, and hardware behavior are Windows-only release
checks rather than properties established by Linux publish.

For a live-runtime or hardware verification session, record the applicable
details below. For an offline inspector or replay session, record only the OS,
.NET version, LTB commit, exact command, and redacted input-fixture identity.

- Windows version and architecture;
- SteamVR and ALVR versions, plus the OpenVR binding or backend version;
- active HMD and controller emulation mode;
- tracker models and firmware versions, with serials redacted;
- LTB version, commit, package SHA-256, and exact command line; and
- whether any `TrackingOverrides` entry was active before setup.

## Native runtime lifecycle

- [ ] **Load and initialize the pinned binding.** Publish with the supported
  `win-x64` profile above and confirm that the output-root `openvr_api.dll`
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
  Record the current OpenVR properties used for the decision. The supported
  tuple is driver and tracking system `oculus`, manufacturer `Oculus`, model
  `Miramar (Left Controller)` for the left role and
  `Miramar (Right Controller)` for the right role, and controller type
  `oculus_touch`. Confirm that a wrong-hand Miramar model, missing property, or
  different controller type is rejected rather than treated as ambiguous.

- [ ] **Prove current ALVR availability independently of OpenVR emulation.**
  With the ALVR dashboard web server on its default port, confirm
  `http://127.0.0.1:8082/api/version` returns a successful, nonempty local
  response. Stop ALVR, return an empty/error response if safely reproducible,
  and change the dashboard port in separate startup runs; confirm `daily`
  remains fail-closed with `DependencyUnavailable`. Restore port `8082`
  afterward. Confirm the production probe issues no more than one request per
  second. Version 0.1 has no configurable ALVR-port CLI option.

- [ ] **Use current observations rather than stored runtime/model claims.** With
  the endpoint healthy, remove or alter one current Miramar/`oculus_touch`
  property while leaving stored `controller_runtime` and `controller_model`
  unchanged. Confirm readiness fails with `DevicesUnavailable`. Restore the
  live tuple and confirm the current recalibration observations are `ALVR` and
  `Quest 2 Touch`; stored values are comparison inputs, not availability proof.

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

## Milestone 3 two-hand calibration wizard

Milestone 3 adds deterministic Linux coverage for the UI-neutral wizard state
machine, reversed-order serial association, guided coverage metrics, the real
lag/alignment/Auto solver pipeline, mixed per-hand model selection, schema-1
profile persistence, and serial-and-hand reload. The scripted command uses
only fake pose streams and fake serials:

```bash
dotnet run --project src/Ltb.App -- wizard-demo --profiles <profile-store.json> [--log <events.jsonl>]
```

This evidence is not a live SteamVR result. In particular, scripted dependency
and apply messages do not show that ALVR exposes original Touch poses, that VMT
accepts both transforms, or that SteamVR activates both overrides. Do not mark
any item below complete from Linux tests alone. Milestone 3 does not yet include
a production `ICalibrationWizardRuntime` implementation for SteamVR capture and
two-hand VMT/override application. The items below are acceptance requirements
for that future Windows composition, not checks currently runnable through
`wizard-demo`. This is a guided-wizard limitation; the implemented production
`daily` ALVR and saved-profile gates are verified separately in the Milestone 4
section below.

### Guided capture and association

- [ ] **Verify scripted wizard JSONL behavior (fake-only).** Run
  `wizard-demo` twice with the same `--log <events.jsonl>` path and confirm the
  second event sequence is appended rather than replacing the first. Run once
  without `--log` and confirm no default event file is created. With fake
  wizard inputs, exercise missing controller position, poor translation
  observability, and bad rotation; confirm the log uses respectively
  `NoPositionAvailable`, `PoorTranslationObservability`, and
  `BadRotationCalibration`. This check does not require SteamVR or hardware.

- [ ] **Run the full two-hand guided gesture.** With both original Touch poses
  visible and overrides released, complete the left-only then right-only
  pitch, yaw, roll, and moderate-translation prompts. Confirm coverage updates
  remain responsive, distinguish orientation/tracking validity from position
  validity, and do not accept elapsed time alone as adequate excitation.

- [ ] **Associate by motion rather than device order.** Start SteamVR with the
  trackers enumerated in each order, repeat the two isolated gestures, and
  confirm the same stable serial remains assigned to each physical hand.
  Record redacted per-candidate correlation, lag, rejection, and selected
  serial evidence. Move both trackers together and induce weak motion in
  separate runs; both cases must stop at `Ready` with an ambiguity or weak-
  correlation diagnostic instead of guessing.

- [ ] **Exercise validity loss during capture.** Occlude one Touch controller,
  occlude one tracker, and disconnect a candidate in separate runs. Confirm
  orientation and position validity fall independently, invalid samples do not
  inflate coverage, and disconnected or repeatedly invalid trackers cannot be
  assigned.

### Per-hand solve, selection, and quality

- [ ] **Validate a full 6DoF hand.** Capture observable multi-axis motion with
  reliable Touch position. Confirm the reported lag and accepted rotation,
  translation observability, physically plausible translation, held-out
  position improvement, full-6DoF selection reason, and quality block. Compare
  the applied lever arm with the measured mount at varied orientations.

- [ ] **Validate normal rotation-only fallback.** Hide or invalidate Touch
  position while retaining tracked orientation, then repeat with deliberately
  translation-degenerate motion. Confirm both runs accept rotation, report
  distinct missing-position or poor-observability reasons, save exactly zero
  translation, and continue to apply rather than label the fallback a failure.

- [ ] **Reject bad rotation separately.** Use static, single-axis, and
  deliberately corrupted orientation captures. Confirm each fails before
  translation/apply, returns to `Ready`, preserves the prior active profiles,
  and gives a direct retry diagnostic.

### Persistence, apply, and reuse

- [ ] **Persist and apply both hands.** Complete one valid first run and inspect
  a redacted profile store. Confirm schema version 1, exact semantic hand and
  tracker serial keys, Auto policy, selected mode and reason, `T_T_C` in meters
  and normalized `XYZW`, lag, quality, and UTC creation time for each hand.
  Confirm both VMT transforms and intended hand overrides become active only
  after both profiles validate and save.

- [ ] **Reuse profiles after enumeration churn and restart.** Restart LTB and
  then SteamVR, reconnect trackers in the opposite order, and confirm exact
  serial-and-hand matching takes the no-capture apply path. A transient OpenVR
  index change must not affect selection. Swap the physical hand association
  or remove one stored side and confirm reuse is refused and recalibration is
  requested.

- [ ] **Exercise profile and apply failures safely.** Test an unsupported
  schema, malformed/truncated store, denied save, one rejected profile, and a
  two-hand apply failure. Confirm bounded diagnostics, no partial store is
  accepted, the prior file remains recoverable, and neither hand is left with
  a newly active stale override. The scripted Milestone 3 runtime is still not
  a live two-hand SteamVR wizard composition. Verify stored-profile two-hand
  application separately through the production `daily` command below; live
  guided capture remains deferred.

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

## Milestone 4 reliable daily use

Milestone 4 includes a production `daily` composition, while its complete
transition matrix is exercised with deterministic fakes on Linux. Run the
production path from an extracted package with this exact command form:

```text
Ltb.App.exe daily --profiles <profile-store.json> --left-vmt-slot <0..57> --right-vmt-slot <0..57> --steamvr-settings <steamvr.vrsettings> [--log <events.jsonl>] [--monitor-rate <hz>] [--reconnect-delay <seconds>]
```

Use two distinct VMT slots from `0` through `57`. `--monitor-rate` defaults to
`20` Hz and `--reconnect-delay` to `0.25` seconds. The runtime's internal pose-
staleness threshold is `0.5` seconds; its VMT heartbeat/discovery bound is five
seconds, and every independent cleanup operation has a two-second bound. For
evidence runs, supply `--log <events.jsonl>`; the sink appends JSON Lines, while
omitting `--log` disables the event file. The checks below prove the Windows
deployment, runtime, and hardware behavior. Do not mark them complete from
transition-matrix tests or a successful cross-publish alone.

### Portable package acceptance

- [ ] **Inspect the self-contained package.** Build with
  `bash build/package-win-x64.sh <version>`. Verify the ZIP checksum, extract it
  on Windows x64, and compare `release-manifest.txt` with the requested version,
  source commit, `source_tree_dirty=false`, `net8.0`, `win-x64`,
  `runtime_framework_version=8.0.28`, the actual runtime pack, .NET SDK,
  Python and zlib versions, `self_contained=true`,
  `publish_single_file=false`, and `publish_trimmed=false`. Confirm
  `openvr_api.dll` matches the pinned hash and
  `licenses/Valve.OpenVR.LICENSE.txt` is byte-identical to the vendored source
  license. Treat a runtime-pin update as a new release validation event: rerun
  build/test/publish and every applicable Windows runtime and hardware check.

- [ ] **Launch without a machine-wide .NET runtime or SDK.** On a representative
  clean Windows x64 account, run `Ltb.App.exe --help` and `Ltb.App.exe devices`
  from the complete extracted directory. Confirm no .NET installation prompt,
  native-library search-path workaround, installer action, or administrator
  elevation is required. Repeat from a directory containing spaces.

- [ ] **Verify package boundaries.** Confirm the ZIP contains no build cache,
  symbols, source tree, settings, backups, logs, recordings, device identities,
  credentials, or owner-local paths. Record the unsigned status and resulting
  SmartScreen behavior without describing the package as signed or installed.

### Startup sequencing

- [ ] **Observe the healthy later-run sequence.** With matching saved profiles
  and all dependencies available, run the exact `daily` command above with two
  distinct slots and `--log <events.jsonl>`. Record structured events for
  `Stopped -> DependencyCheck -> WaitingForSteamVR -> WaitingForDevices -> Ready
  -> ApplyProfile -> Active`. Confirm no override is active before
  `ApplyProfile` and `Active` appears only after the complete apply succeeds.

- [ ] **Start before SteamVR.** Launch LTB with SteamVR stopped. Confirm it waits
  in `WaitingForSteamVR` with no active mapping. Start SteamVR, allow device
  enumeration to settle, and confirm the remaining transitions occur once,
  without manual settings edits or duplicate VMT sources.

- [ ] **Start with incomplete devices.** Repeat with each required tracker,
  Touch controller, and VMT surface absent. Confirm `WaitingForDevices`, a
  distinct actionable event, and no partial apply. Confirm an unavailable ALVR
  version endpoint reports `DependencyUnavailable`, missing or unsupported
  Touch/tracker observations report `DevicesUnavailable`, and a missing or stale
  VMT heartbeat reports `VmtUnavailable`. Restore the exact stable identities
  and confirm the normal apply gates rather than a transient-index shortcut.

- [ ] **Distinguish calibration outcomes.** Produce controller-position
  absence, poor translation observability, bad rotation, and active tracker
  loss in separate controlled runs. Confirm different structured codes and
  results: the first two are successful rotation-only fallbacks, bad rotation
  returns to `Ready`, and tracker loss enters `SafeDisable`.

### Reconnect and watchdog acceptance

- [ ] **Reacquire a lost tracker by stable serial.** While active, power off or
  safely occlude one tracker. Confirm `SafeDisable` completes before
  `WaitingForDevices`, the virtual hand is not left frozen, and reconnect with
  a changed transient index succeeds only for the same stable serial. A
  same-class substitute must not be accepted. Confirm the full
  `Ready -> ApplyProfile -> Active` path on recovery.

- [ ] **Reacquire Touch input without pose-only continuation.** Disconnect one
  Touch controller while tracker and VMT remain healthy. Confirm SafeDisable
  disables both daily VMT profiles and releases both LTB-owned mappings rather
  than leaving either tracker pose active without the intended inputs.
  Reconnect the same controller/role and verify input provenance again after
  the complete two-hand reapply.

- [ ] **Recover from VMT loss.** Stop the VMT heartbeat or driver, remove the
  active VMT device, and change its identity in separate runs. Confirm each
  case enters `SafeDisable`, attempts both cleanup surfaces, waits for the
  dependency/device, and reapplies only after the expected source is healthy.

- [ ] **Stop terminally if SteamVR ends during VMT recovery.** After VMT loss
  completes SafeDisable and enters dependency/runtime recovery, stop SteamVR
  before VMT becomes ready. Confirm `SteamVrStopped -> Stopped`, no OpenVR
  session reopen, no second `ApplyProfile` or `Active`, and process exit code
  `3` when cleanup and rollback have no failures. A cleanup or rollback failure
  must remain exit code `4` rather than being hidden by the runtime stop.

- [ ] **Handle SteamVR stop and restart.** Stop SteamVR during `Active`.
  Confirm `SafeDisable -> Stopped`, no claim that persistent settings vanished
  with the runtime, and no frozen virtual hand after the runtime returns. Start
  a new SteamVR session, rerun `daily`, and confirm fresh enumeration and
  stable-serial profile reuse rather than old transient-index reuse. The first
  invocation must not resume after SteamVR returns.

- [ ] **Exercise OpenVR quit-event handling.** Trigger a runtime quit event and
  confirm LTB acknowledges it through OpenVR, records `SteamVrStopped`, enters
  `SafeDisable`, and stops. Trigger a driver-requested quit and confirm the same
  stopped classification. If a process-quit event for another OpenVR client can
  be induced safely, confirm LTB ignores it rather than disabling a healthy
  session.

- [ ] **Measure watchdog timing.** For every available tracker, Touch, VMT pose,
  VMT heartbeat, ALVR local-version proof, current OpenVR controller tuple, and
  SteamVR failure, record observation time, event time, SafeDisable
  start/completion, effective monitor rate, and configured freshness bound.
  Confirm the ALVR HTTP probe remains capped at 1 Hz, cleanup begins within the
  implemented bound, and no transition leaves an active override with a stale
  source.

- [ ] **Bound cleanup without skipping work.** Stall VMT deactivation and
  settings release separately. Confirm each operation is bounded to two
  seconds, a timeout is reported as a cleanup failure, and the other cleanup
  surface is still attempted. Confirm exit code `4` and manually inspect both
  slots and both LTB-owned mappings before reuse.

### Apply rollback, shutdown, and logging

- [ ] **Verify unexpected runtime-failure ordering with fakes.** Inject an
  unexpected adapter exception during the active monitor loop. Confirm an error
  `RuntimeFailure` event records only `exceptionType` and `exceptionMessage`,
  with no stack trace, before `SafeDisableStarted` and before the final
  `Stopped` transition. Confirm every active VMT deactivation and exact mapping
  release is still attempted. The coordinator result is `RuntimeFailure` when
  cleanup succeeds and `SafeDisableFailed` when cleanup fails. This ordering
  check is portable and does not require Windows hardware.

- [ ] **Force each two-hand apply failure position.** Reject the first hand,
  reject the second after the first succeeds, and fail rollback after a partial
  apply. Confirm `Active` is never emitted for an incomplete pair, effects from
  the attempt are rolled back when possible, rollback failures are distinct,
  exit code `4` reports any rollback failure, and both hands are inspected
  before reuse. Run this through the production `daily` adapter; the fake
  coordinator result alone is insufficient.

- [ ] **Verify settings and profile rollback together.** Exercise malformed
  settings, denied ACL, lock contention, external-writer race, post-write
  validation failure, malformed profile store, denied profile save, and
  application failure. Confirm no partial JSON, unrelated settings survive,
  an external winner is never overwritten by automatic rollback, previous
  profiles remain recoverable, and a reviewed recovery creates its own undo
  backup. Confirm rollback restores only effects from the current two-hand
  application attempt and never accepts a partial pair as `Active`.

- [ ] **Verify clean shutdown.** From healthy `Active`, request normal shutdown
  and confirm `SafeDisable -> Stopped`, VMT deactivation, exact mapping release,
  zero cleanup failures, and restoration of the original Touch pose. Repeat
  during dependency/device waiting and during apply; no path may report
  `Stopped` while silently leaving a newly active override.

- [ ] **Verify unmanaged-termination recovery.** In a non-worn controlled
  setup, terminate only LTB so managed cleanup cannot run. Confirm the process
  makes no SafeDisable claim. On the next start, verify cleanup occurs before a
  new apply and activation is blocked if cleanup cannot be confirmed. Record
  the documented manual recovery boundary for console destruction, OS crash,
  and power loss without inducing destructive failures.

- [ ] **Validate structured events and privacy.** For every state and failure
  transition, confirm stable event code, severity, state, message, UTC
  timestamp, and appropriate hand/dependency context. Verify repeated events
  are bounded and ordering is sufficient to reconstruct apply, rollback,
  SafeDisable, and reconnect. Run twice with the same `--log` path and confirm
  the second JSONL sequence is appended rather than truncating the first; then
  omit `--log` and confirm no default event file is created. Induce a log-write
  failure and confirm cleanup and rollback still run. Export a redacted
  diagnostic set and confirm it contains no telemetry upload, credentials, raw
  settings/backups, recordings, real serials, or owner-local paths.

### Consolidated specification 23.4 hardware acceptance

- [ ] **Static alignment at varied orientations.** Compare the physical Touch
  reference and virtual output at several static yaw, pitch, and roll poses for
  both hands. Record position and rotation error without exposing serials.

- [ ] **Rapid pitch, yaw, and roll.** Exercise fast but safe multi-axis motion.
  Confirm stable output, live Touch inputs, no discontinuity from quaternion
  sign handling, and no false reconnect or stale-source interval.

- [ ] **In-place wrist rotation with an offset tracker.** Use a measured,
  nonzero mount lever arm and rotate around the grip. Confirm full 6DoF applies
  `T_L_tracker * T_T_C`; repeat with rotation-only and confirm zero translation
  preserves the tracker origin as documented.

- [ ] **Aiming and tool alignment in multiple applications.** Test at least two
  representative SteamVR applications with varied poses and motion. Record
  application versions and redacted observations; one runtime home view is not
  sufficient acceptance.

- [ ] **Controller input coverage.** Exercise buttons, trigger, grip, stick
  axes, and capacitive inputs where exposed while VMT supplies pose. Confirm
  every input still originates from the intended Touch controller.

- [ ] **Repeated startup without recalibration.** Complete multiple managed
  start/active/shutdown cycles in one SteamVR session. Confirm saved profiles
  reuse by stable serial, no routine manual settings edit, no duplicate source,
  and no accumulating stale mapping.

- [ ] **Profile reuse after reboot.** Reboot Windows, start the complete runtime
  stack, and reuse both profiles after device-index churn. Confirm the mount,
  hand, and controller observations still satisfy recalibration policy and the
  intended stable serials are selected.

- [ ] **Tracker battery loss.** During active use, safely remove tracker power
  or allow a controlled battery-loss condition. Confirm bounded watchdog
  detection, SafeDisable, no permanently frozen virtual hand, stable-serial
  reacquisition after power returns, and successful reapply only after all
  health gates pass.
