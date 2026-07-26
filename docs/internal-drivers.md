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
controller-source trackers, calibration profiles, and the driver feed. Normal
**Start** reuses an exact matching left/right profile pair. The separate
**Calibrate / Recalibrate** button creates the same first-party session while
explicitly bypassing reusable profiles and capturing both hands again. Fresh
association scores every connected physical tracker during separate left- and
right-hand prompts and accepts only one unambiguous distinct pair. Unrelated
raw Lighthouse trackers may remain connected and are ignored after association
or saved-profile selection. Neither action starts the legacy
ALVR/VMT/`TrackingOverrides` wizard.

The stopped/pre-session panel reads every SteamVR `config` root from the
current user's `openvrpaths.vrpath`, enumerates only `generic_tracker` records
from each exact lowercase `lighthouse` paired-device directory, and shows
typed missing, unreadable, malformed, duplicate, or empty diagnostics without
throwing through the GUI. A config-array entry without that directory does not
veto another applicable root; missing is reported only when no configured root
contains it. The owner can select and save one complete distinct left/right
pair or explicitly choose **Use automatic association**. Stored and reported
serials are uppercase canonical values; comparisons are ordinal
case-insensitive. The manual pair and lifecycle policy are application
settings in `internal-driver.json`, never calibration-profile fields.

Before pressing **Start**:

1. While the LTB session is stopped, press **Refresh**, review any typed paired-
   tracker or registration-state diagnostic, and save a complete distinct
   manual pair or retain automatic association.
2. If a manual pair is saved, exit SteamVR completely. **Start** refuses before
   session creation while either `vrserver` or `vrmonitor` is running and
   writes no SteamVR setting.
3. Start the official Meta Horizon Link PC application and establish Quest
   Link or Air Link.
4. Keep the headset and both Touch controllers awake.
5. For the automatic-association path, start SteamVR with the intended
   Lighthouse HMD as the sole HMD.
6. Power on the two controller-mounted Lighthouse trackers and wait until
   their raw poses are valid. Unrelated full-body trackers may remain connected;
   during new association move only the mounted controller requested by each
   prompt so the left/right correlation pair is unambiguous.
7. Run `Ltb.Gui.exe` from the complete extracted package and press **Start**.

A manual binding currently cannot pass the next preflight stage. Paired
`config.json` proves the selected uppercase serial and model, but it does not
prove the live registered-device path/key SteamVR uses in its tracker-role
settings. LTB reports this typed unresolved state and performs no
`steamvr.vrsettings` write. The exact lowercase `lighthouse` directory is
paired-device enumeration evidence only; LTB does not treat it as
registered-path evidence, synthesize `/devices/lighthouse/<serial>`, or invent
a serial/path cache. The required Windows evidence is described in the
verification checklist.

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
translation only when observable, saves schema-3 profiles, then starts a fresh
IPC feed. No position or poor translation observability may validly select
rotation-only; bad rotation coverage or quality is a failure. Existing valid
schema-2 first-party profiles remain reusable with identity mount adjustments
without capture or an automatic profile rewrite.

When a manual binding is available to a session after its exact-path preflight,
motion correlation verifies the owner's choice. Agreement is reported as
verification. A mismatch presents the correlated pair as an explicit
correction candidate; the owner must accept that correction or explicitly
retain the manual pair. Correlation never silently swaps a saved pair. With no
manual binding, existing automatic association remains unchanged.

Explicit recalibration stages both hand results against a private copy of the
profile store. Cancellation and canonical commit share one explicit decision
boundary after both hands validate: cancellation that wins before that boundary
leaves the prior pair and unrelated profiles unchanged with no stage residue.
Once commit wins, the atomic canonical replace completes and is reported as a
successful saved pair; a later Stop request applies to the session, not to the
already committed profile transaction.

The mount-adjustment panel provides left/right tracker-side and
controller-side edits in millimeters and degrees. Human-entered rotations are
intrinsic local `X`, then `Y`, then `Z`, with
`q = Qx * Qy * Qz`. Edits apply immediately to
`X_eff = A_tracker * X_mount * A_controller`, including pose and lever-arm
linear velocity from one immutable App snapshot. Reset affects only the chosen
slot. Dirty state remains visible across same-revision lifecycle events, and
the profile store changes only after **Save** succeeds. Saving upgrades the
active schema-2 pair to schema 3; a failed Save does not relabel the current
live/saved snapshot.

While stopped, the panel can request left-only, right-only, or both-hand
calibration. A selected-hand run requires one reusable opposite-hand profile
but no prior profile for the selected hand. It captures only the requested
hand, scores every viable contender, adds or replaces only that selected key,
and preserves the opposite and unrelated serialized profile records.

The GUI presents readiness, per-hand tracker/input/publication state, neutral
reasons, the shared calibration phase, and feed health. The structured JSONL
log is the durable evidence surface for exact staged/loaded identities, stable
HMD metadata, per-hand capture measurements, selected calibration mode and
reason, lag, and quality metrics.

Use **Stop** before changing runtime or hardware state. Closing the window also
requests the same bounded fail-safe stop and waits for session cleanup. A
stopped or closed session is never reused; the next **Start** creates a new
session. **Calibrate / Recalibrate** is available only while stopped and cannot
overlap Start, driver removal, or window close.

## Automatic paths

The supported desktop flow has no editable device-index or integration-path
fields. From a packaged build, the default paths are:

| Purpose | Path |
|---|---|
| Staged SteamVR driver | `driver_ltb` beside `Ltb.Gui.exe` |
| Settings | `%LOCALAPPDATA%\LighthouseTouchBridge\settings\internal-driver.json` |
| Calibration profiles | `%LOCALAPPDATA%\LighthouseTouchBridge\profiles\calibration-profiles.json` |
| Registration receipts | `%LOCALAPPDATA%\LighthouseTouchBridge\driver\registration-receipts.json` |
| Tracker-role recovery receipt | `%LOCALAPPDATA%\LighthouseTouchBridge\driver\tracker-role-recovery.json` |
| Structured log | `%LOCALAPPDATA%\LighthouseTouchBridge\logs\internal-driver.jsonl` |

`manual_tracker_binding` and `unregister_on_exit` live only in the settings
file above. A missing `unregister_on_exit` member in compatible old settings
loads as `true`; the settings writer then emits the explicit value.
Calibration profiles continue to contain calibration and mount-adjustment
evidence only.

```json
{
  "manual_tracker_binding": {
    "left_tracker_serial": "LHR-LEFT",
    "right_tracker_serial": "LHR-RIGHT"
  },
  "unregister_on_exit": true
}
```

The binding object is omitted when automatic association is selected. The
illustrative serials above are placeholders, not hardware evidence.

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
| Runtime composition | `X_eff = A_tracker * X_mount * A_controller`; `T_output = T_tracker * X_eff` |
| Driver pose time | Monotonic nanoseconds mapped from `Stopwatch`/QPC |
| Clock alignment | Paired Meta-time and QPC samples establish and refresh the mapping |

Each hand uses its own `ovrPoseStatef.TimeInSeconds`. `SensorSampleTime` is not
a substitute. Wall time is used only for human-readable provenance.

Every observation enumerates the current connected physical tracker roster and
reads that roster through one shared OpenVR pose acquisition. The reusable
batch access handle is rebuilt whenever a stable serial, registered device
path, or transient access index changes. Immediately before and after the
native batch acquisition, the production runtime re-reads serial and path for
every requested index under the same runtime critical section; index reuse or
an identity change fails the entire observation closed. The production path
requests raw, uncalibrated poses with a zero prediction offset. The 10 ms
managed loop uses absolute monotonic deadlines and skips missed slots, so
observation and publication work do not accumulate as `work time + 10 ms`
drift.

The session snapshot and JSONL record expose additive `timing` evidence:
iteration interval, observation duration, pair-publication duration, each
selected tracker's host-ingress age at its publication boundary, and observed
tracker count. `is_software_lower_bound` is always true: these measurements
cover only application-observed software boundaries. They do not measure
device sampling, SteamVR/compositor, display scanout, or motion-to-photon
latency, and they do not add pose prediction or extrapolation.

## Local IPC and fail-safe behavior

IPC v1 is a fixed-layout, little-endian protocol over a local Windows named
pipe. The pipe admits only the owning Windows session. Each producer start uses
a new unpredictable session identifier and sequence zero. Both endpoints
reject malformed, non-finite, out-of-range, replayed, or time-regressing data
without partially updating device state.

The producer sends heartbeats even when state does not change. Global session
liveness and per-hand state freshness are separate. After 500 ms without any
valid state or heartbeat, `driver_ltb` marks both devices untracked. After
500 ms without a valid state for one hand, it marks only that hand untracked
and neutralizes its inputs; heartbeat or other-hand traffic cannot keep that
hand's stale state alive. When pipe-server setup fails transiently inside
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

### Physical tracker role neutralization

The physical-tracker role transaction has one narrow intended settings
responsibility: in `steamvr.vrsettings`, set exactly the two bound controller-
source trackers' entries in the top-level `trackers` object to
`TrackerRole_None`. Valve's
[OpenVR driver documentation](https://github.com/ValveSoftware/openvr/blob/master/docs/Driver_API_Documentation.md#trackers-full-body-tracking)
defines that object and requires each key to be the full registered device path
`/devices/<driver_name>/<device_serial_number>`.

For a saved manual binding, this transaction is a pre-session operation. Both
`vrserver` and `vrmonitor` must be stopped. LTB accepts exactly two distinct
registered paths only after each uppercase bound serial's serial-to-live-path
relationship is proven by an authoritative live descriptor or equally strong
stored evidence with explicit provenance and defensible freshness. Paired
`lighthouse/*/config.json` alone is insufficient. The current application has
no such offline authority, so it fails closed before any tracker-role write.
It never constructs a path from a serial or driver name and has no unproven
cache.

Valve's documented tracker-role list does not include `TrackerRole_None`. The
neutral value is supported here by observed deployed-configuration evidence:
Antilatency's
[published OpenVR driver configuration](https://developers.antilatency.com/Software/OpenVR_Driver_en.html#override)
shows full device-path entries in `trackers` set to `TrackerRole_None`. That is
deployment evidence, not an explicit normative Valve guarantee about the
value.

`TrackerRole_None` remains unverified for this product on the target Windows
SteamVR runtime. In particular, LTB does not yet claim that VRChat or another
application ignores a tracker with that value. The checklist separately
requires capture of exactly what SteamVR writes for **Disabled** in Manage
Trackers and an application-level ignored-tracker proof.

After exact path authority is established, the transaction snapshots the
`trackers` object's prior presence plus each target's exact prior presence and
JSON value, writes and verifies both neutral entries together, and restores
that exact state during cleanup: any prior JSON value is restored verbatim,
including `null` or an object, while a previously absent entry is removed. An
object created solely for the transaction is also removed when it was
originally absent. The operation preserves unrelated tracker entries and all
unrelated settings and uses the existing settings lock, sibling backup,
same-directory temporary write, read-back verification, compare-before-commit
guard, rollback, and `FindRecoveryBackups` recovery boundary.

The production App persists a separate recovery receipt before the role write.
The receipt identifies the exact settings file, original hash, expected
transaction-owned neutral post-image, exact two paths, pre-existing backup
set, and owned backup once known. A later startup calls `FindRecoveryBackups`
before activation and restores only an unambiguous owned backup whose bytes
match the captured original. If the current file instead reflects an external
writer, automatic recovery is refused, the receipt remains for inspection, and
the GUI retains an explicit restore-failure warning. If a prior process
restored the original but crashed before deleting the receipt, the next
startup verifies the original hash and safely clears the receipt.

Session activation publishes neutralizing and active state, and cleanup
publishes restoring/restored or restore-failed state. Stop, disposal, desktop
window close, activation failure, Meta/driver recovery, and stable
serial/device-path churn all restore before a new neutralization. The warning
is cleared only by explicit successful recovery/restore evidence; a generic
stopped snapshot cannot erase it.

This transaction does not configure, create, remove, or repair ALVR, VMT, or
SteamVR `TrackingOverrides`; those remain unsupported legacy integration
material. It changes no tracker-role entry other than the two caller-supplied
physical tracker paths. Automated coverage of the file transaction is not
Windows SteamVR runtime evidence, so the live Windows checklist remains
required.

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

The registration transaction does not stop SteamVR itself. When a manual
binding exists, the earlier pre-session gate refuses session creation until
both `vrserver` and `vrmonitor` are stopped. Without a manual binding, existing
registration behavior remains: a registration written while SteamVR runs may
be reverted by SteamVR's own shutdown rewrite of `steamvr.vrsettings` or
`openvrpaths.vrpath`, so the next run re-registers idempotently and an initial
run may need a second restart. To register in one pass, press **Start** once
while SteamVR is stopped, then start SteamVR; the session registers first and
waits for the runtime.

### Registration lifecycle policy and next-start inspection

The desktop setting **Unregister driver_ltb on Stop or exit** is enabled by
default, including when compatible older settings omit the property. A
controlled Stop or window exit awaits session fail-safe cleanup, then removes
only registration roots whose authority comes from a durable LTB receipt or
complete exact LTB artifact proof. The next **Start** re-registers
`driver_ltb` and may require one SteamVR restart. The owner may save an
explicit opt-out to retain registration.

If SteamVR is still running during removal, the GUI says that removal takes
effect only after SteamVR restarts. It never says the already-loaded
controllers disappeared live.

Every refresh and next Start inspects the complete external-driver list and
all durable LTB receipts. A crash can therefore surface a receipt-only root, a
receipt/registration mismatch, or duplicate registrations before another
session proceeds. Duplicate roots are removed automatically only when every
target is independently receipt-owned or exact-artifact-proven and one
transaction can preserve all unrelated registration entries in their original
order. A non-canonical alias, stale mismatch, or partially proven duplicate
fails closed with an actionable diagnostic.

Registration cleanup never modifies unrelated external drivers, including
`01spacecalibrator`, `bigscreenbeyond`, `vmt`, and `alvr_server`, nor does it
restore or revive ALVR, VMT, or `TrackingOverrides`.

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
Removal deletes only exact canonical LTB roots authorized before mutation,
restores the applicable receipt-recorded `activateMultipleDrivers` state,
verifies the result, and rolls back on failure; unrelated drivers and user
configuration are never modified. A registration made by an older build
without a receipt is removed only after its complete staged artifacts prove
that exact root is LTB's own driver directory; without a pre-registration
snapshot, `activateMultipleDrivers` is deliberately left unchanged. A SteamVR
restart completes the removal. If SteamVR was running, no live device
disappearance is claimed before that restart.

Linux automation and portable C++ tests cover protocol, fake Meta input,
registration transactions, cross-language decoding, frame/quaternion rules,
session rollover, malformed/range/NaN/replay cases, timeout, and neutral safety.
They are not Windows runtime or hardware evidence. Complete and retain the
[Windows internal-driver verification checklist](windows-internal-driver-verification.md)
on the target machine.

The tracked checklist contains 91 live acceptance items: the 91 lines that
begin with `- [ ]`. All 91 remain unchecked, and the repository does not
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
- Non-canonical or equivalent path aliases cannot be assigned safe automatic
  removal authority and therefore fail closed for manual inspection.
- Native device publication is unchanged. OpenVR may allow later
  `TrackedDeviceAdded` calls in principle, but LTB has not verified that path:
  managed startup currently waits for both activated controller serial/build
  identities before creating the authenticated pipe peer, while the native
  provider creates the receiver and both devices during `Init`. OpenVR also
  offers no atomic two-device add/removal rollback after `Init`.
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
