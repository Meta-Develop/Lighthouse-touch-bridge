# First-Party Internal Driver Operations

## Status and support boundary

This is the operational reference for the current LTB default. The first-party
implementation and automated tests are present, but the Windows hardware and
runtime gates have not been recorded as passed. Use the fully unchecked
[Windows internal-driver verification checklist](windows-internal-driver-verification.md)
before making any Windows compatibility or release claim.

The supported deployment is Windows x64. Its only accepted external runtime
dependencies are SteamVR and the official Meta Horizon Link PC runtime. LTB
does not depend on ALVR, VMT, or SteamVR `TrackingOverrides`, does not install a
headset application, carries no video, and does not register Quest as a
SteamVR HMD or controller provider.

```text
Quest + Touch
  -> official Meta Horizon Link runtime
  -> Ltb.MetaLink
  -> Ltb.App calibration and pose composition
  -> same-user local named pipe
  -> driver_ltb
  -> SteamVR
```

SteamVR must receive exactly two LTB controller devices. Their inputs come from
the Meta runtime; their runtime poses come from the paired Lighthouse trackers.
The intended Lighthouse HMD remains SteamVR's sole HMD.

## Default desktop workflow

The packaged `Ltb.Gui.exe` starts directly in the **First-party internal
driver** view. Its **Start** button creates a fresh application session and
runs typed checks for Windows, SteamVR, driver registration and loaded build,
Meta Link, the sole Lighthouse HMD, both Touch hands, the two selected
controller-source trackers, calibration profiles, and the driver feed. Other
raw Lighthouse trackers are ignored after saved profiles select the
controller-mounted pair. It never starts the legacy
ALVR/VMT/`TrackingOverrides` wizard.

Before pressing **Start**:

1. Start the official Meta Horizon Link PC application and establish Quest
   Link or Air Link.
2. Keep the headset and both Touch controllers awake.
3. Start SteamVR with the intended Lighthouse HMD as the sole HMD.
4. Power on the two controller-mounted Lighthouse trackers and wait until
   their raw poses are valid. Saved profiles allow unrelated full-body
   trackers to remain connected; new association/calibration still requires
   exactly two candidates so the first pair is unambiguous.
5. Run `Ltb.Gui.exe` from the complete extracted package and press **Start**.

Once both LTB controllers are ready, the physical left Touch Menu button opens
and closes the SteamVR dashboard through OpenVR's reserved system input. VRChat
menu actions remain on the application binding (Y/B by default); changing that
binding is not required for the SteamVR dashboard.

LTB transactionally registers the staged `driver_ltb` directory beside the
application. If registration changed, the GUI reports **Restart required**.
Stop LTB, restart SteamVR once, and press **Start** again so the runtime loads
the staged build. Readiness does not pass until the loaded left and right
controllers both report the exact staged build identity. On the very first
registration a run started while SteamVR is already up may need a second
SteamVR restart; see
[Registration and verification](#registration-and-verification).

On a first run or after a recalibration trigger, LTB captures the hands
separately. Move only the requested mounted controller continuously through
pitch, yaw, and roll; add moderate translation while keeping the controller
visible to the Quest cameras if full 6DoF is desired. LTB associates the two
trackers from real motion, estimates residual lag, validates rotation, attempts
translation only when observable, saves schema-2 profiles, then starts a fresh
IPC feed. No position or poor translation observability may validly select
rotation-only; bad rotation coverage or quality is a failure.

The GUI presents readiness, per-hand tracker/input/publication state, neutral
reasons, the shared calibration phase, and feed health. The structured JSONL
log is the durable evidence surface for exact staged/loaded identities, stable
HMD metadata, per-hand capture measurements, selected calibration mode and
reason, lag, and quality metrics.

Use **Stop** before changing runtime or hardware state. Closing the window also
requests the same bounded fail-safe stop and waits for session cleanup. A
stopped or closed session is never reused; the next **Start** creates a new
session.

## Automatic paths

The supported desktop flow has no editable device-index or integration-path
fields. From a packaged build, the default paths are:

| Purpose | Path |
|---|---|
| Staged SteamVR driver | `driver_ltb` beside `Ltb.Gui.exe` |
| Settings | `%LOCALAPPDATA%\LighthouseTouchBridge\settings\internal-driver.json` |
| Calibration profiles | `%LOCALAPPDATA%\LighthouseTouchBridge\profiles\calibration-profiles.json` |
| Registration receipts | `%LOCALAPPDATA%\LighthouseTouchBridge\driver\registration-receipts.json` |
| Structured log | `%LOCALAPPDATA%\LighthouseTouchBridge\logs\internal-driver.jsonl` |

The log appends JSON records, rotates at its configured bound, and may include
hardware identities. Redact stable identities and owner-local paths before
sharing evidence.

## Exact Meta Horizon Link discovery

`Ltb.MetaLink` resolves the installed runtime from this exact 32-bit registry
contract on 64-bit Windows:

```text
Key:   HKLM\SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus
Value: Base
```

A current Meta Horizon-branded installation may register:

```text
Base = C:\Program Files\Meta Horizon\
```

The x64 runtime must then exist at:

```text
<Base>Support\oculus-runtime\LibOVRRT64_1.dll
```

For the example above, that resolves to
`C:\Program Files\Meta Horizon\Support\oculus-runtime\LibOVRRT64_1.dll`.
LTB loads that complete resolved path and requests the public LibOVR ABI 1.64
(minor version 64).

Both older Oculus-branded and current Meta Horizon-branded install roots are
supported only when the installer records their absolute root in the registry
`Base` value. LTB does not probe or fall back to
`C:\Program Files\Oculus`, `C:\Program Files\Meta Horizon`, the current
directory, `PATH`, or a filename-only DLL load.

Discovery failures are readiness failures with direct remediation:

- a missing, blank, or non-absolute `Base` value reports `NotInstalled` and
  asks the user to install or repair Meta Horizon Link registration;
- a registered root without
  `Support\oculus-runtime\LibOVRRT64_1.dll` reports `NotInstalled` and asks
  the user to repair Meta Horizon Link; and
- an incompatible or unloadable registered DLL reports `AbiUnavailable` with
  an install/repair/update diagnostic.

Do not copy a DLL into the LTB directory or hand-edit a fallback path. Repair
the official installation and its registration, then run a fresh session.

## Manual headset and controller wake guidance

LTB automates no ADB operation. It does not run ADB, change headset power or
proximity-sensor settings, install a headset component, or promise to keep the
headset or controllers awake.

For a controlled calibration or verification session:

- establish Quest Link or Air Link while the headset is awake, and confirm
  both controllers respond in the Meta runtime before starting LTB;
- keep the headset in the state required by the current official Link workflow
  and periodically move or use both controllers so their input state remains
  available;
- if the proximity sensor or automatic sleep interrupts Link, use only the
  supported headset, Meta Horizon Link, or Meta Quest Developer Hub (MQDH) UI
  controls documented for the installed versions to adjust the behavior
  manually; and
- record any temporary keep-awake or proximity-sensor change and restore it
  after the test.

MQDH is optional and is not an LTB dependency. Its UI and available device
controls can change between releases, so follow current official Meta guidance.
This project intentionally supplies no ADB command and does not recommend
inventing one from old forum instructions.

## Modules and dependency boundaries

| Module | Responsibility | Allowed dependencies |
|---|---|---|
| `Ltb.MetaLink` | Load registered LibOVR and sample Touch state | Meta native ABI and narrow .NET interop only |
| `Ltb.Protocol` | Encode, decode, and validate IPC v1 | BCL only; no runtime SDK dependency |
| `Ltb.Driver` | Publish the C# feed and own transport, readiness, and registration | `Ltb.Protocol` plus narrow OS and OpenVR registration boundaries |
| `native/driver_ltb` | Expose two SteamVR controller devices and consume IPC | OpenVR driver API and C++ protocol code |

`Ltb.App` owns tracker-to-hand association, mount calibration, pose
composition, and feed publication. `Ltb.Gui` is a presentation layer over the
typed application session; it does not sequence runtimes itself.
`Ltb.Calibration` remains portable and deterministic and has no UI, SteamVR,
OpenVR, Meta Link, driver, pipe, or application dependency.

## Frame, transform, and clock contract

| Property | Contract |
|---|---|
| Handedness and axes | Right-handed; `+X` right, `+Y` up, `-Z` forward |
| Translation | Meters |
| Angles and angular velocity | Radians and radians per second |
| Quaternion storage | `(x, y, z, w)`, finite and normalized before publication |
| Transform meaning | Active parent-from-child transforms |
| Runtime composition | `T_output = T_tracker * X_mount` |
| Driver pose time | Monotonic nanoseconds mapped from `Stopwatch`/QPC |
| Clock alignment | Paired Meta-time and QPC samples establish and refresh the mapping |

Each hand uses its own `ovrPoseStatef.TimeInSeconds`. `SensorSampleTime` is not
a substitute. Wall time is used only for human-readable provenance.

## Local IPC and fail-safe behavior

IPC v1 is a fixed-layout, little-endian protocol over a local Windows named
pipe. The pipe admits only the owning Windows session. Each producer start uses
a new unpredictable session identifier and sequence zero. Both endpoints
reject malformed, non-finite, out-of-range, replayed, or time-regressing data
without partially updating device state.

The producer sends heartbeats even when state does not change. After 500 ms
without a valid state or heartbeat, `driver_ltb` marks both devices untracked
and neutralizes every input. When pipe-server setup fails transiently inside
`driver_ltb`, the receiver retries with capped exponential backoff from 1 s up
to 30 s rather than abandoning the transport. Reconnect uses a new session; it
never resumes a stale session or frozen pose. Loss of one associated tracker
neutralizes only that hand while exact-serial reacquisition is attempted.
Unrelated tracker connection, disconnection, or device-index churn does not
change the selected controller pair. Loss of Meta readiness neutralizes both
hands.

`driver_ltb` performs no calibration or Meta access. It publishes exactly two
stable left/right controller roles with the LTB input profile. Haptics are not
advertised and LibOVR controller battery state is reported as absent.

## Registration and verification

Driver registration snapshots the external-driver state, registers the exact
staged path, enables `activateMultipleDrivers`, verifies the result, and rolls
back on failure. When LTB registers `driver_ltb`, it also persists a
registration receipt at
`%LOCALAPPDATA%\LighthouseTouchBridge\driver\registration-receipts.json`
recording the canonical driver path and the prior `activateMultipleDrivers`
state, so removal keeps its authority across application restarts.

LTB's own writes to `steamvr.vrsettings` and `openvrpaths.vrpath` stage a
temporary file in the same directory with fsync and read-back verification,
then commit it with an atomic rename. A crash at any point leaves either the
complete old or the complete new content, never a truncated file. Two residual
limits are documented and accepted: the exclusive handle used for the content
comparison must be released just before the rename, so another process's write
landing in that small window is overwritten by the commit; and directory-entry
durability after the rename depends on filesystem journaling, the same
limitation as `AtomicFileWriter`.

### Package import boundary

Windows driver packages statically link their compiler runtimes. The package
target and Windows CI run a PE-import gate over the exact staged
`driver_ltb.dll`; accepted imports are limited to an explicit Windows system
DLL allowlist and API-set names. Compiler runtime DLLs are neither allowlisted
nor staged beside the driver.

Linux tests prove the PE parser and allowlist policy, including rejection
paths, and run only the portable native CTest targets. They do not prove the
import table of a Windows-produced driver. The Windows driver workflow must
build the actual staged `driver_ltb.dll`, inspect its regular and delay-load
imports, and pass that exact artifact through the package gate before it can be
used as import evidence.

Registration runs at session start and does not check whether SteamVR is
running. SteamVR rewrites `steamvr.vrsettings` and `openvrpaths.vrpath` from
memory when it exits, so a registration written while SteamVR runs can be
reverted by SteamVR's own shutdown; the next LTB run re-registers
idempotently. A first run started while SteamVR is up may therefore need two
SteamVR restarts before `driver_ltb` loads. To register in one pass, press
**Start** once while SteamVR is stopped, then start SteamVR; the session
performs registration first and waits for the runtime afterwards.

### Driver removal

Removal is a first-class transactional operation, available without SteamVR
file editing or manual `vrpathreg` use:

- Desktop: the **Remove driver** button in the **Driver registration
  maintenance** panel (the session must be stopped first).
- Command line: `dotnet run --project src/Ltb.App -- remove-driver` (or
  `Ltb.App.exe remove-driver` from a packaged build). Exit codes: `0` removed
  or nothing to remove, `2` refused or failed with a completed rollback, `4`
  incomplete rollback.

Removal authority is the registration receipt LTB persists at
`%LOCALAPPDATA%\LighthouseTouchBridge\driver\registration-receipts.json` when
it registers `driver_ltb`, so removal works after any application restart.
Removal deletes only the exact canonical LTB driver path, restores the
`activateMultipleDrivers` presence and value recorded before LTB's
registration, verifies the result, and rolls back on failure; unrelated
drivers and user configuration are never modified. A registration made by an
older build without a receipt is removed only after the staged driver
artifacts prove the path is LTB's own driver directory, and the
`activateMultipleDrivers` setting is then deliberately left unchanged. A
SteamVR restart completes the removal.

Linux automation and portable C++ tests cover protocol, fake Meta input,
registration transactions, cross-language decoding, frame/quaternion rules,
session rollover, malformed/range/NaN/replay cases, timeout, and neutral safety.
They are not Windows runtime or hardware evidence. Complete and retain the
[Windows internal-driver verification checklist](windows-internal-driver-verification.md)
on the target machine.

The tracked checklist contains 59 live acceptance items: the 59 lines that
begin with `- [ ]`. All 59 remain unchecked, and the repository does not
assign them to additional evidence categories. Existing automated and Linux
evidence does not satisfy the Windows runtime and connected-hardware gates in
specification sections 23.3 and 23.4 or Definition of Done item 14. The next
registration, load, and removal verification run starts from the fresh
environment and clean baseline defined in that checklist.

## Known limitations and backlog

These behaviors are deliberate current trade-offs, recorded so they are not
mistaken for unnoticed defects:

- `openvrpaths.vrpath` verification after registration requires the exact
  prior `external_drivers` order with the LTB path appended last. A
  `vrpathreg` that reorders entries would make registration fail in the safe
  direction (rollback), not corrupt state.
- Driver-root path equivalence checks are textual after canonicalization.
  Symlink or 8.3 short-name aliases of the same directory evade duplicate
  detection.
- A single non-advancing LibOVR clock observation triggers a full Meta session
  teardown with backoff before reconnection. This is fail-safe but heavy for
  what may be a one-sample stall.
- Rewrites of the owned SteamVR settings files drop JSON comments and any BOM.
  SteamVR tolerates both outcomes.
- GitHub Actions workflows pin actions to major version tags, not commit SHAs.

## Legacy migration material

The ALVR, VMT, and SteamVR `TrackingOverrides` implementation is retained
historical migration material. The full legacy paths remain runnable behind
the `legacy-*` CLI commands, each of which prints an unsupported-path warning
before executing; they stay available until the Windows exit gates pass and
are then scheduled for removal. The legacy paths receive no new setup,
configuration, recovery, packaging, or daily-use support and are not invoked
by the first-party GUI **Start** button. Existing detail is preserved in the
[legacy setup reference](setup.md),
[legacy troubleshooting reference](troubleshooting.md), and
[legacy Windows checklist](windows-verification.md); none of those documents
defines the supported first-party path.
