# Windows Internal-Driver Verification

This checklist is the release-evidence gate for the first-party Meta Horizon
Link plus `driver_ltb` path. It contains no completed hardware/runtime claim:
every checkbox is intentionally unchecked until a controlled Windows run
records the required evidence.

Use [Internal driver operations](internal-drivers.md) for the supported setup
and discovery contract. The older
[ALVR/VMT Windows checklist](windows-verification.md) is compile-only migration
history and cannot satisfy any item here.

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
  one right, plus exactly two eligible physical Lighthouse tracker sources and
  no unexpected LTB device.
- [ ] Introduce an additional HMD, Meta/ALVR controller, unexpected LTB device,
  or third physical tracker in controlled tests and confirm readiness fails
  closed and publication remains neutral.
- [ ] Confirm the raw tracker observations use the intended uncalibrated/raw
  tracking universe and stable serial identities through device-index churn.

## Driver staging, registration, and loaded identity

- [ ] Record the exact nonblank build identity in the staged
  `driver_ltb/build-id.txt` and confirm the staged manifest and x64 DLL are from
  the same package.
- [ ] From an unregistered state, press **Start** and confirm LTB registers the
  exact staged driver root transactionally, preserves unrelated external
  drivers, enables `activateMultipleDrivers`, and requests one SteamVR restart.
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
- [ ] Remove `driver_ltb` through the supported lifecycle and confirm prior
  LTB-owned registration/settings state is restored and a required SteamVR
  restart is reported.

## Per-hand association and calibration evidence

- [ ] Begin with no matching schema-2 profiles and confirm the GUI requests a
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
  creation time from the retained schema-2 profile evidence.
- [ ] For each full-6DoF result, also record position RMS in millimeters and
  translation condition number. Confirm full 6DoF is not reported without
  both metrics.
- [ ] Produce missing/unreliable Meta position or poor translation
  observability and confirm a valid result selects rotation-only with zero
  translation and a direct reason rather than claiming full 6DoF.
- [ ] Produce poor rotation coverage/quality and confirm calibration fails
  rather than treating it as a rotation-only success.
- [ ] Stop and start without moving either mount; confirm both exact schema-2
  profiles are reused with their original selected mode, lag, quality, tracker
  identity, and `ltb_touch` driver profile.
- [ ] Move or swap a mount and confirm the affected profile is rejected or
  recalibration is requested rather than silently reusing an incompatible
  transform.

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
- [ ] Restore transport and confirm only fresh, ordered state recovers; replay,
  regressing timestamps, malformed packets, and an old session remain rejected
  without partial state publication.
- [ ] Lose one associated tracker and confirm only that hand neutralizes, no
  other tracker is substituted, and recovery requires the exact stable serial.
- [ ] Introduce invalid tracker topology and confirm both hands neutralize
  until exactly the associated pair remains.
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
