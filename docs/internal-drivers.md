# Internal Driver Bridge Design Contract

## Status and goal

This document defines a **target design**, not a claim that the internal bridge
is implemented or hardware-verified. The supported deployment is Windows;
SteamVR and official Meta Quest Link are its only external runtime dependencies.
Standalone-headset modes are out of scope.

```text
Quest + Touch
  -> Meta Quest Link runtime
  -> Ltb.MetaLink
  -> C# bridge composition
  -> local named pipe
  -> driver_ltb
  -> SteamVR
```

Quest Link only keeps Touch controllers connected and exposes their state. LTB
installs no headset app, carries no video, and never registers Quest as the
SteamVR HMD. SteamVR receives exactly two controller devices: Meta inputs with
runtime poses from the paired Lighthouse trackers.

## Modules and dependency boundaries

| Module | Target responsibility | Allowed dependencies |
|---|---|---|
| `Ltb.MetaLink` | Load the Meta PC runtime and sample Touch state | Meta native ABI and narrow .NET interop only |
| `Ltb.Protocol` | Encode, decode, and validate IPC v1 | BCL only; no runtime SDK dependency |
| `Ltb.Driver` | Publish the C# feed and own transport, readiness, and registration | `Ltb.Protocol` plus narrow OS and OpenVR registration boundaries |
| `native/driver_ltb` | Expose two SteamVR controller devices and consume IPC | OpenVR driver API and C++ protocol code |

`Ltb.App` owns hand pairing, mount calibration, pose composition, and feed
publication through these modules. `Ltb.Calibration` remains portable and
deterministic. It must not depend on Meta, OpenVR, SteamVR, the driver, pipes,
UI, or application composition. Platform integrations stay behind narrow
interfaces; ABI structs must not leak into calibration models.

## Meta Link ABI and readiness

- The supported baseline is the official Meta PC package version `32.0.0`,
  which exposes C ABI `1.64`.
- `Ltb.MetaLink` targets `LibOVRRT64_1.dll` and requests minor ABI version `64`.
- Load the DLL from the complete installation path derived from the Meta runtime
  registry entry, never the search path, current directory, or filename alone.
- Meta runtime binaries are not redistributed. A compatible user-installed
  Quest Link runtime is a prerequisite.
- Each hand uses its own `ovrPoseStatef.TimeInSeconds` as the pose timestamp.
  `SensorSampleTime` must not substitute for the per-hand timestamp.
- Startup and periodic paired samples map Meta time to `Stopwatch`/QPC time;
  interpolation and IPC timestamps use that monotonic mapping, never wall time.
- Controller battery data is unavailable through this ABI and is transmitted
  as not present.
- Automatic keep-awake behavior is out of scope. The UI may instruct the user
  to keep Quest Link and the controllers awake manually.

Readiness is explicit: `NotInstalled`, `AbiUnavailable`, `RuntimeStopped`,
`HeadsetDisconnected`, `ControllersUnavailable`, `Ready`, and `Faulted`. Only
`Ready` permits publication. Losing readiness neutralizes inputs and marks the
devices untracked until recovery starts a new session.

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

The bridge must name the parent and child frames at every conversion boundary.
No implicit handedness flip, quaternion reordering, unit scaling, or transform
order change is permitted.

## Local IPC v1

All fields are little-endian. The message header and hand-state payload are
fixed-layout and versioned; unknown message types are rejected.

| Segment | Fields |
|---|---|
| Header | magic, version, message type, payload length |
| Ordering | session identifier, sequence number, monotonic nanoseconds |
| Identity | hand, presence and tracking flags |
| Pose | position `(x,y,z)`, quaternion `(x,y,z,w)` |
| Motion | linear velocity `(x,y,z)`, angular velocity `(x,y,z)` |
| Digital input | buttons bitset, capacitive touches bitset |
| Analog input | trigger, grip, stick `(x,y)` |
| Optional telemetry | battery-present flag, battery value |

The transport is a local Windows named pipe. Its ACL permits only the owning
user; remote clients are rejected.
The producer sends heartbeats even when state is unchanged. Each producer
start creates a new unpredictable session identifier and resets sequence to
zero; sequence ordering applies only within that session.

Both endpoints reject bad magic/version/type/length, truncated messages,
non-finite values, invalid enum or bit ranges, out-of-range analog values,
non-unit/degenerate quaternions, replayed sequences, and regressing timestamps.
Bounds are defined once in the shared protocol tests. No rejected packet may
partially update device state.

After 500 ms without a valid state or heartbeat, both devices become untracked
and every button, touch, trigger, grip, and stick value becomes neutral. A
fresh valid state may recover only under the current session and ordering
rules; reconnecting with a new session starts from sequence zero.

## Driver and registration contract

`driver_ltb` is a thin consumer: it validates protocol and freshness and
publishes SteamVR properties/inputs, but performs no calibration or Meta access.
It registers exactly two devices with stable left/right roles and a dedicated
LTB profile. Haptics are unsupported and must not be advertised.

Installation uses `vrpathreg`/`openvrpaths.vrpath` through a transactional tool.
It snapshots the external-driver state, verifies the new path, and rolls back
on failure. Removal restores prior state without deleting unrelated drivers.
The target is `activateMultipleDrivers = true`; rollback/uninstall also restores
its previous value.

## Verification gates and legacy fallback

Linux CI uses a fake Meta source and fake pipe plus managed and C++ protocol
tests covering golden bytes, cross-language decoding, frame/quaternion rules,
session rollover, malformed/range/NaN/replay cases, timeout, and neutral safety.

Windows release gates require loading the installed Meta DLL by its resolved
path, proving ABI `1.64`, observing per-hand timestamps under Quest Link,
exercising reconnect/readiness transitions, loading `driver_ltb` in SteamVR,
checking both roles/profile/inputs/poses, and proving registration rollback.
These are hardware/runtime gates and cannot be replaced by Linux fakes.

The existing ALVR, VMT, and SteamVR `TrackingOverrides` path remains a buildable
fallback only until the Meta Link and `driver_ltb` path passes every Windows
gate above. It receives no new configuration or orchestration automation and is
not a verified operational fallback until its own Windows hardware checklist
passes. The internal path likewise remains target/design until every Windows
gate records evidence.
