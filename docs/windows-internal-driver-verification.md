# Windows Internal-Driver Verification

This checklist is the release-evidence gate for the first-party Meta Horizon
Link plus `driver_ltb` path. It contains no completed hardware/runtime claim:
every checkbox is intentionally unchecked until a controlled Windows run
records the required evidence.

Use [Internal driver operations](internal-drivers.md) for the supported setup
and discovery contract. The older
[ALVR/VMT Windows checklist](windows-verification.md) is legacy migration
history for paths that run only behind warning-gated `legacy-*` commands and
cannot satisfy any item here.

## Repository-visible evidence boundary

This tracked checklist contains 91 live acceptance items: the 91 lines below
that begin with `- [ ]`. All 91 remain unchecked. The repository does not
assign these items to additional evidence categories, so no category subtotals
are claimed here. Existing automated and Linux evidence does not satisfy the
Windows runtime and connected-hardware gates in specification sections 23.3
and 23.4 or Definition of Done item 14.

Driver packages statically link their compiler runtimes. The package target
and Windows CI gate the exact staged `driver_ltb.dll` by parsing regular and
delay-load PE imports and permitting only an explicit Windows system DLL
allowlist and API-set names. No compiler runtime DLL is staged. Linux tests
prove the parser and allowlist logic and run the portable native CTest targets
only. The Windows workflow must build and inspect the actual produced
`driver_ltb.dll` before its import set is accepted.

## Fresh-environment transaction precondition

Start the next registration, load, and removal verification run from a fresh
environment, record a clean baseline, and retain evidence for this complete
transaction:

1. Prove a clean initial state: no LTB or SteamVR process, LTB scheduled task,
   LTB external-driver registration, or registration receipt for the staged
   driver.
2. Create protected-file backups of `openvrpaths.vrpath` and
   `steamvr.vrsettings`; verify their hashes and record the original external
   driver order and relevant settings.
3. Record the exact package/source commit, build identity, compiler, generator,
   flags, binary hash, PE format, exports, complete import table, and successful
   PE-import gate result for the staged Windows-produced `driver_ltb.dll`.
4. With SteamVR stopped, register through LTB and verify the fresh registration
   receipt. Then prove SteamVR loaded that exact staged DLL and exposed exactly
   `LTB-TOUCH-LEFT` and `LTB-TOUCH-RIGHT`, with the intended left/right roles,
   profile, and staged build identity and with no extra LTB device.
5. Stop SteamVR and run receipt-backed removal from a fresh process; require
   verified exit code `0` and removal of only LTB-owned registration state.
6. Restore both protected files byte-for-byte and verify their hashes and the
   unrelated external-driver order against the clean baseline recorded in
   steps 1 and 2.
7. Prove final residue absence: no LTB or SteamVR process, scheduled task,
   external-driver registration, registration receipt, temporary setting, or
   staged test residue remains.

Any unresolved import, missing intended-DLL load or exact-controller proof,
unexplained shutdown, protected-state difference, incomplete receipt-backed
removal, or residue fails this transaction. Passing this software transaction
still does not replace the connected-hardware gates.

## Evidence record

- [ ] Record the Windows version, SteamVR version, Meta Horizon Link version,
  Quest firmware, LTB package version/source commit, `driver_ltb` staged build
  identity, and test date.
- [ ] Record the HMD, Quest, Touch-controller, tracker, and base-station models
  without publishing real serial numbers or owner-local paths.
- [ ] Preserve redacted GUI screenshots, structured JSONL records, SteamVR
  runtime observations, registration before/after state, and failure notes for
  every completed section.
- [ ] Confirm the package under test is an intact win-x64 package containing
  `Ltb.Gui.exe`, `driver_ltb/driver.vrdrivermanifest`,
  `driver_ltb/build-id.txt`, and `driver_ltb/bin/win64/driver_ltb.dll`.

## Meta Horizon Link runtime and Touch input

- [ ] Confirm the official Meta Horizon Link PC application is installed and
  its runtime service is running.
- [ ] Confirm
  `HKLM\SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus` contains an absolute
  string `Base` value; record a redacted value and do not substitute a guessed
  `Program Files` root.
- [ ] Confirm `<Base>Support\oculus-runtime\LibOVRRT64_1.dll` exists and LTB
  loads that exact registered file, requests minor ABI 64, and reports ABI 1.64
  readiness.
- [ ] Remove or invalidate the registration in a controlled reversible test
  and confirm LTB reports a clear missing-registration readiness error without
  probing `C:\Program Files\Oculus` or another fallback.
- [ ] Remove or isolate the registered DLL in a controlled reversible test and
  confirm LTB reports the distinct missing-DLL readiness error without loading
  a copied or filename-only DLL.
- [ ] Establish Quest Link or Air Link and confirm both left and right Touch
  hands report `Ready`, current per-hand pose timestamps, valid analog input,
  buttons, capacitive touches, trigger, grip, and stick state.
- [ ] Exercise controller sleep/wake and Link disconnect/reconnect. Confirm
  loss is reported per runtime/hand, inputs become neutral, and recovery starts
  a fresh Meta/IPC session rather than reusing stale state.
- [ ] Confirm the test record identifies any manual headset keep-awake,
  proximity-sensor, or MQDH setting and confirms LTB ran no ADB automation.

## SteamVR topology and sole Lighthouse HMD

- [ ] Start SteamVR with Bigscreen Beyond 2/2e as the sole HMD and confirm LTB
  records its stable device identity/path plus positive Lighthouse driver or
  tracking-system evidence, without treating a transient OpenVR index as
  identity.
- [ ] Confirm Quest is absent from SteamVR as an HMD and that no Meta-, Oculus-,
  or ALVR-provided controller device is present.
- [ ] Confirm SteamVR exposes exactly two LTB input controllers, one left and
  one right, plus the two selected controller-source Lighthouse trackers and
  no unexpected LTB device.
- [ ] Introduce an additional HMD, Meta/ALVR controller, unexpected LTB device,
  or ambiguous/missing selected tracker in controlled tests and confirm
  readiness fails closed and publication remains neutral.
- [ ] With saved profiles, connect five raw Lighthouse trackers (the two
  selected controller trackers plus three full-body trackers), press **Start**,
  and confirm the exact left/right pair is reused without capture and both LTB
  controllers remain active; connect/disconnect or re-index only the three
  unrelated trackers and confirm neither selected hand is neutralized.
- [ ] Confirm the raw tracker observations use the intended uncalibrated/raw
  tracking universe and stable serial identities through device-index churn.

## Stopped tracker binding and neutralization authority

- [ ] With the LTB session stopped, exercise every `config` root listed in
  `openvrpaths.vrpath` and confirm **Refresh** enumerates only
  `device_class = generic_tracker` records from the exact case-sensitive
  `Lighthouse` directories. Force missing, unreadable, malformed, duplicate-
  serial, and empty cases and retain each typed GUI diagnostic; none may escape
  as an unhandled exception.
- [ ] Save one complete distinct manual left/right binding from mixed-case
  source serials and confirm `internal-driver.json` stores both serials in
  uppercase canonical form, ordinal case-insensitive matching rejects the same
  serial on both sides, and no calibration profile field or byte changes.
- [ ] With a manual pair, capture separate left/right motion-correlation
  agreement and mismatch. Confirm agreement is verification only; a mismatch
  presents a complete correction candidate and changes the pair only after an
  explicit **Accept correction** decision, while **Retain manual pair** keeps
  the saved binding.
- [ ] Clear the manual binding and confirm the existing automatic association
  behavior, ambiguity rejection, and saved-profile selection remain unchanged.
- [ ] With a manual binding present, run `vrserver` alone and then `vrmonitor`
  alone. In each case press **Start** and confirm preflight refuses before
  session creation, names the running process, and leaves
  `steamvr.vrsettings` byte-for-byte unchanged.
- [ ] In SteamVR **Manage Trackers**, set one physical tracker to
  **Disabled** and capture exactly which file, object, key, and JSON value
  SteamVR writes, including the before/after bytes and behavior after restart.
  Do not assume the value is `TrackerRole_None`.
- [ ] Establish the real relationship from each paired `config.json`
  `device_serial_number` and `model_number` to the live OpenVR registered-
  device path/key. Repeat across SteamVR restart and transient device-index
  churn and retain provenance strong enough to distinguish observation from a
  serial-derived guess.
- [ ] Before that relationship is available to LTB, press **Start** with a
  manual pair and confirm the typed registered-path-unresolved diagnostic,
  zero `steamvr.vrsettings` writes, no lowercase pairing-directory fallback,
  no `/devices/lighthouse/<serial>` synthesis, and no invented path cache.
- [ ] After authoritative serial-to-live-path evidence is integrated, confirm
  preflight writes exactly the two bound registered paths in one transactional
  operation, preserves every unrelated tracker role/setting, and retains the
  existing backup, receipt, rollback, and crash-recovery authority.
- [ ] With the exact two physical trackers intentionally neutralized, prove in
  VRChat and at least one other representative application that neither
  tracker is consumed while both LTB controllers remain current. Record this
  as runtime evidence; do not infer it from the settings value alone.

## Driver staging, registration, and loaded identity

- [ ] Record the exact nonblank build identity in the staged
  `driver_ltb/build-id.txt` and confirm the staged manifest and x64 DLL are from
  the same package.
- [ ] From an unregistered state with SteamVR stopped (the recommended
  first-registration flow), press **Start** and confirm LTB registers the
  exact staged driver root transactionally, preserves unrelated external
  drivers, enables `activateMultipleDrivers`, and requests one SteamVR restart.
- [ ] In a controlled test, register for the first time while SteamVR is
  running and confirm the documented behavior: SteamVR's own shutdown rewrite
  of `steamvr.vrsettings`/`openvrpaths.vrpath` may revert the registration,
  the next run re-registers idempotently, and `driver_ltb` loads after at most
  a second SteamVR restart.
- [ ] Confirm readiness remains blocked while restart is required; restart
  SteamVR, press **Start** again, and confirm the restart-required gate clears.
- [ ] Confirm the loaded left controller has the exact stable serial
  `LTB-TOUCH-LEFT` and the loaded right controller has
  `LTB-TOUCH-RIGHT`.
- [ ] Confirm both controllers report a runtime build identity that exactly
  equals the staged build identity. A missing marker, missing controller,
  duplicate device, or identity mismatch must fail closed.
- [ ] Stage a different valid build in a controlled update and confirm SteamVR
  must restart before the new exact staged/loaded identity can pass.
- [ ] Force a registration failure and confirm the prior external-driver list
  and prior `activateMultipleDrivers` value are restored without deleting an
  unrelated driver.
- [ ] Remove `driver_ltb` with the **Remove driver** button in the **Driver
  registration maintenance** panel (session stopped) and confirm only the
  exact canonical LTB path is removed, the recorded prior
  `activateMultipleDrivers` presence and value is restored, unrelated drivers
  and settings are untouched, and a required SteamVR restart is reported.
- [ ] After a full application restart, register and then remove with
  `Ltb.App.exe remove-driver` and confirm the persisted registration receipt
  at `%LOCALAPPDATA%\LighthouseTouchBridge\driver\registration-receipts.json`
  supplies the removal authority; record exit code `0` for removed or nothing
  to remove, `2` for refused or failed with completed rollback, and `4` for an
  incomplete rollback in forced-failure tests.
- [ ] Load compatible old `internal-driver.json` data with no
  `unregister_on_exit` member and confirm the GUI defaults the policy to
  enabled. Save both enabled and disabled values and confirm deterministic
  round-trip without changing manual binding, paths, or calibration profiles.
- [ ] With unregister-on-exit enabled, complete a controlled **Stop** and a
  controlled window exit in separate runs. Confirm each awaits session
  fail-safe cleanup, removes only independently authorized LTB registration
  roots, and reports that the next **Start** re-registers `driver_ltb` and may
  require one SteamVR restart.
- [ ] Disable and save unregister-on-exit, then repeat controlled **Stop** and
  window exit. Confirm the `driver_ltb` registration and its receipt remain
  intact while normal session cleanup still completes.
- [ ] Remove while SteamVR is running and confirm the GUI says removal takes
  effect only after restart, does not claim either already-loaded LTB device
  disappeared live, and the post-restart external-driver/device state matches
  the completed transaction.
- [ ] Terminate LTB after it has persisted a registration receipt but before a
  normal controlled cleanup. On the next launch, confirm the stopped panel
  inspects and surfaces the exact receipt-only, registered-only, or
  receipt/registration-mismatch state before another Start proceeds.
- [ ] Create multiple `driver_ltb` registrations whose every canonical root is
  independently receipt-owned or complete-artifact-proven. Confirm next Start
  removes all and only those roots transactionally, preserves unrelated
  registration order and settings, and requires the documented restart.
- [ ] Repeat duplicate registration with one unproven root, a stale receipt
  mismatch, and a non-canonical equivalent path. Confirm each case fails closed
  with actionable ownership diagnostics and performs no partial cleanup.
- [ ] Keep unrelated external drivers named `01spacecalibrator`,
  `bigscreenbeyond`, `vmt`, and `alvr_server` registered through default
  cleanup, duplicate cleanup, rollback, and next-start re-registration.
  Confirm their exact paths, order, and unrelated settings remain unchanged.
- [ ] After default Stop/exit cleanup, press **Start** with SteamVR stopped,
  confirm `driver_ltb` is re-registered from the exact staged root, restart
  SteamVR once when requested, and verify the exact two staged-build
  controller identities before readiness passes.

## Per-hand association and calibration evidence

- [ ] Begin with no matching compatible first-party profiles and confirm the GUI requests a
  separate left-hand capture followed by a separate right-hand capture.
- [ ] For the left hand, record sample count, tracking/orientation/position
  validity fractions, motion-axis coverage, total rotation, rotation progress,
  position progress, and readiness from the structured log. Confirm the values
  come from strictly monotonic real Meta pose samples.
- [ ] Repeat the complete capture-evidence record for the right hand.
- [ ] Confirm insufficient pitch/yaw/roll coverage fails with an actionable
  per-hand diagnostic; then repeat with adequate continuous multi-axis motion.
- [ ] Confirm tracker-to-hand association follows the separate real motion and
  not enumeration order, display name, or transient device index.
- [ ] For each hand, record estimated residual lag in milliseconds, selected
  mode, selection reason, rotation RMS in degrees, inlier ratio, and profile
  creation time from the retained schema-3 profile evidence.
- [ ] For each full-6DoF result, also record position RMS in millimeters and
  translation condition number. Confirm full 6DoF is not reported without
  both metrics.
- [ ] Produce missing/unreliable Meta position or poor translation
  observability and confirm a valid result selects rotation-only with zero
  translation and a direct reason rather than claiming full 6DoF.
- [ ] Produce poor rotation coverage/quality and confirm calibration fails
  rather than treating it as a rotation-only success.
- [ ] Stop and start without moving either mount, including with three
  unrelated trackers still connected; confirm exact compatible schema-2 or
  schema-3 profiles are reused with their original selected mode, lag, quality,
  tracker identity, and `ltb_touch` driver profile. For a schema-2 fixture,
  confirm identity adjustment evidence and byte-for-byte no-rewrite reuse.
- [ ] Move or swap a mount, stop the session, and press **Calibrate /
  Recalibrate** with the two mounted trackers and three unrelated trackers
  connected; confirm both hands are captured instead of reusing the saved pair,
  and confirm the mounted tracker serials win the separate left/right motion
  correlations. Move an unrelated tracker in sync with a prompted hand and
  confirm ambiguous association fails without selecting either hand. Force the
  right-hand attempt to fail after left validation and confirm the complete
  prior pair and unrelated store entries remain byte-for-byte unchanged, then
  repeat successfully and confirm the new pair replaces the prior pair
  together with no staging residue.

## Mount adjustment and exact tracker-role recovery

- [ ] Enter `Active` with exactly one associated left tracker and one
  associated right tracker, record their registered device paths, and confirm
  only those exact two entries become `TrackerRole_None`; unrelated tracker
  role entries and all unrelated settings must remain unchanged.
- [ ] With the exact two physical trackers neutralized, run VRChat and confirm
  VRChat no longer consumes either as a full-body tracker while the two LTB
  controllers continue to provide current pose and input.
- [ ] Edit noncommuting per-hand tracker-side and controller-side trim in
  millimeters/degrees and confirm the live pose plus rotating lever-arm linear
  velocity change from one coherent effective-transform snapshot, with no
  torn generation or discontinuity.
- [ ] Confirm unsaved trim remains visibly dirty and survives unrelated
  same-revision status updates without changing profile bytes. Press **Save**,
  restart LTB, and confirm schema-3 persistence and the exact effective
  transform; separately confirm normal schema-2 reuse stays identity and
  byte-exact until Save.
- [ ] In separate controlled runs, confirm **Stop**, desktop window close, and
  activation failure restore both exact original tracker-role entries and
  leave unrelated roles/settings unchanged.
- [ ] Terminate LTB after neutralization without managed cleanup, restart it,
  and confirm startup processes the durable owned receipt and restores the
  exact original settings before a new activation. Also exercise the
  restore-completed/receipt-delete crash window and confirm startup clears the
  already-restored receipt safely.
- [ ] Force settings to differ from the transaction-owned neutral post-image
  and confirm automatic recovery refuses to overwrite the external writer,
  retains the receipt, and keeps an explicit visible restore-failure warning
  through generic stopped/status updates until successful restore/recovery.
- [ ] Run left-only and right-only calibration separately. For each, confirm
  only the requested hand is captured and committed, a new selected hand need
  not have a prior profile, and the retained opposite profile plus unrelated
  serialized profile objects remain byte-for-byte unchanged. Repeat both-hand
  calibration to confirm its existing all-or-nothing pair semantics.

## Feed, reconnect, watchdog, and shutdown fail-safe

- [ ] On activation, confirm the feed begins with a new unpredictable session
  identifier, global sequence zero, and current monotonic timestamps.
- [ ] Confirm both hands publish current composed poses and complete Touch
  input while heartbeat age and last-send age remain healthy.
- [ ] Interrupt the named pipe or unload/reload the driver and confirm the GUI
  enters reconnecting, both devices become neutral/untracked as required, and
  recovery uses a new session identifier with sequence reset to zero.
- [ ] Prevent valid state and heartbeat delivery for more than 500 ms and
  confirm `driver_ltb` marks both devices untracked and neutralizes every
  button, touch, trigger, grip, and stick value.
- [ ] Continue valid heartbeats while withholding left `HandState` for more
  than 500 ms. Confirm the left device becomes untracked and neutral while the
  right remains current; heartbeat traffic must not refresh left-hand state.
- [ ] Continue valid right `HandState` traffic while withholding left
  `HandState` for more than 500 ms. Confirm the left device still becomes
  untracked and neutral; other-hand traffic must not refresh its freshness.
- [ ] Repeat the two per-hand stale checks with left/right exchanged, then
  confirm only a fresh ordered state for the stale hand recovers it.
- [ ] Restore transport and confirm only fresh, ordered state recovers; replay,
  regressing timestamps, malformed packets, and an old session remain rejected
  without partial state publication.
- [ ] Lose one associated tracker and confirm only that hand neutralizes, no
  other tracker is substituted, and recovery requires the exact stable serial.
- [ ] Introduce ambiguous or duplicate selected controller-source identity
  evidence and confirm publication fails closed; unrelated trackers alone must
  not neutralize either selected hand.
- [ ] Lose Meta readiness and confirm both hands neutralize, the old feed is
  retired, and Link recovery creates a fresh feed session.
- [ ] Stop SteamVR during active use and confirm both hands neutralize, the
  session reaches `Stopped`, and LTB does not silently reopen SteamVR or reuse
  the feed when the runtime returns.
- [ ] Press **Stop** during readiness waiting, calibration, and active use in
  separate runs. Confirm bounded cleanup, neutral output, retired runtime
  resources, and no reuse of the stopped session.
- [ ] Close the desktop window during readiness waiting, calibration, and
  active use in separate runs. Confirm close waits for the same fail-safe stop
  before the process exits and leaves no frozen controller state.
- [ ] Terminate the process without managed cleanup in a controlled non-worn
  test and confirm the driver watchdog neutralizes stale state within its
  bound. Record that the process made no false clean-stop claim.

## End-to-end Beyond plus Quest acceptance

- [ ] With Bigscreen Beyond 2/2e, Quest 2 through official Meta Horizon Link,
  Quest 2 Touch controllers, two rigidly mounted Vive Trackers, and Lighthouse
  base stations, complete registration, restart, two-hand calibration, feed
  start, active use, and clean stop through `Ltb.Gui.exe` only.
- [ ] Confirm Beyond remains the sole SteamVR HMD throughout and Quest/Meta
  devices never appear as a second HMD or native SteamVR controller provider.
- [ ] Verify static alignment at varied yaw, pitch, and roll for both hands and
  retain measured position/rotation observations.
- [ ] Verify rapid but safe pitch, yaw, roll, and in-place wrist rotation with
  the nonzero tracker mount offset; confirm no discontinuity, pose freeze, or
  input loss.
- [ ] Exercise every supported button, touch, trigger, grip, and stick input in
  at least two representative SteamVR applications while the Lighthouse
  trackers remain the authoritative pose sources.
- [ ] Press the left Meta Menu button and confirm it opens and closes the
  SteamVR dashboard without simultaneously invoking an application menu.
  Confirm the input profile declares `/input/system` as left-only while
  application bindings and automatic remapping omit it, the right controller
  exposes no dashboard button, and VRChat's Quick Menu remains available from
  Y/B under the bundled binding.
- [ ] Complete repeated start/stop cycles in one SteamVR session and confirm no
  duplicate devices, stale registration, or feed-session reuse.
- [ ] Reboot Windows, restore the same runtime/hardware topology, reuse the
  saved profiles after device-index churn, and confirm exact staged/loaded
  driver identity before active publication.
- [ ] Complete controlled Link loss, one-tracker loss, pipe loss, SteamVR stop,
  GUI stop, and GUI close scenarios without a frozen or input-active stale
  controller.
- [ ] Review all retained evidence, unresolved failures, temporary headset
  settings, and redactions. Leave every failed or unrun item unchecked and do
  not claim Windows support beyond the evidence actually recorded.
