# driver_ltb

`driver_ltb` is the first-party SteamVR controller endpoint for Lighthouse
Touch Bridge. The portable C++20 core parses and validates IPC v1, owns ordered
per-session state, and applies the 500 ms watchdog. The Windows-only shell owns
the same-user named pipe, two stable OpenVR controller devices, and SteamVR
pose/input publication. It performs no calibration, tracker association, Meta
runtime access, or pose composition.

## IPC v1 wire layout

All integers and IEEE-754 single-precision values are little-endian.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII magic `LTB1` |
| 4 | 2 | major version `1` |
| 6 | 2 | minor version `0` |
| 8 | 2 | message type: hand state `1`, heartbeat `2` |
| 10 | 2 | reserved, zero |
| 12 | 4 | payload length |
| 16 | 16 | raw session identifier bytes |
| 32 | 8 | sequence |
| 40 | 8 | producer monotonic nanoseconds |
| 48 | 1 | hand: left `1`, right `2` (hand state only) |
| 49 | 1 | reserved, zero |
| 50 | 2 | state flags |
| 52 | 12 | position `x,y,z` |
| 64 | 16 | quaternion `x,y,z,w` |
| 80 | 12 | linear velocity `x,y,z` |
| 92 | 12 | angular velocity `x,y,z` |
| 104 | 4 | button bits |
| 108 | 4 | capacitive-touch bits |
| 112 | 4 | trigger `[0,1]` |
| 116 | 4 | grip `[0,1]` |
| 120 | 4 | stick X `[-1,1]` |
| 124 | 4 | stick Y `[-1,1]` |
| 128 | 4 | reserved battery float, must be zero |

The header is 16 bytes. The 48-byte heartbeat ends after offset 47 and has
payload length 32. The 132-byte hand state ends after offset 131 and has
payload length 116. Flag bits 0 through 7 are,
in order: connected, orientation valid, position valid, linear velocity valid,
angular velocity valid, inputs valid, reserved battery present, and tracked.
Bit 6 (`BatteryPresent`) is defined for the wire layout but forbidden in IPC v1;
the allowed state mask is `0x00bf`. Battery-present and every nonzero battery
value are rejected. Unknown flags and input bits are rejected.
Touch bits 0 through 6 are primary, secondary, trigger, stick, thumbrest,
index-pointing, and thumb-up states.
Button bits 0 through 4 are primary, secondary, menu, thumbstick click, and
trigger click.
When either velocity-valid flag is clear, its three velocity fields must be
exactly zero. A disconnected state must have all flags clear. Tracked requires
connected plus valid orientation and position. Inputs must be neutral when
inputs-valid is clear. Quaternion norm tolerance is `0.001`.

The layout and scalar widths above match
[`protocol.hpp`](include/ltb_driver/protocol.hpp),
[`protocol.cpp`](src/core/protocol.cpp), the managed
[`ProtocolConstants`](../../src/Ltb.Protocol/ProtocolConstants.cs), and
[`ProtocolCodec`](../../src/Ltb.Protocol/ProtocolCodec.cs). Packet offsets are
wire offsets, not offsets into the native in-memory `HandState` struct.

The Windows pipe name is `\\.\pipe\lighthouse-touch-bridge-v1`, matching the
managed `NamedPipeDriverTransportFactory` default.

The staged driver has one deterministic build identity in the format
`driver_ltb-<major>.<minor>.<patch>-ipc-<major>.<minor>`. CMake derives it from
the stable driver project version and IPC major/minor, then generates both the
compile-time constant published through OpenVR's `Prop_DriverVersion_String`
and the staged `build-id.txt`. The marker and runtime property therefore name
the same version-derived identity without using a timestamp, source path, or
machine state. Current driver version `0.1.1` and IPC `1.0` yield
`driver_ltb-0.1.1-ipc-1.0`; the generated marker ends with one LF. This identity
is not a binary hash. Readiness compares both loaded controller properties with
the staged marker; complete packaging separately verifies the archived DLL
SHA-256 against the Windows-built artifact.

Sequence numbers increase globally within a session. Timestamp monotonicity is
checked independently for heartbeats, left-hand samples, and right-hand
samples: a heartbeat carries producer send time, while a hand packet carries
that hand's mapped pose-sample time. Consequently, a hand sample may be older
than a preceding heartbeat or the other hand's sample. A new, non-retired
session starts at sequence zero and has no timestamp relationship to the
retired session. Watchdog freshness uses local arrival time, not producer time.
At 500 ms without any accepted packet, both hands become untracked and neutral.
Each hand also expires at 500 ms without its own accepted hand state, even
while heartbeats or the other hand remain current. Invalid packets refresh
neither age. Retired-session replay protection in the native state store
retains the last 16 retired identifiers.

The final pose is already composed in OpenVR raw/driver space. The shell keeps
`qWorldFromDriverRotation` at identity and `vecWorldFromDriverTranslation`
at zero. A client-space Standing
pose must not be sent over this protocol.

## Build and test

Run these commands from the repository root. Linux builds the portable core
and its tests, without the OpenVR shell:

```bash
cmake -S native/driver_ltb -B build/native -G Ninja \
  -DLTB_BUILD_OPENVR_DRIVER=OFF -DLTB_BUILD_TESTS=ON
cmake --build build/native
ctest --test-dir build/native --output-on-failure
```

On Windows with Visual Studio 2022, from the repository root:

```powershell
cmake -S native/driver_ltb -B build/native-windows -A x64 -DLTB_BUILD_OPENVR_DRIVER=ON -DLTB_BUILD_TESTS=ON
cmake --build build/native-windows --config Release
ctest --test-dir build/native-windows -C Release --output-on-failure
```

The full shell requires the pinned
`src/Ltb.OpenVr/ThirdParty/OpenVR/openvr_driver.h` in the checkout. MSVC uses
the static compiler runtime; MinGW builds use static runtime link options.
The default build also runs `ltb_driver_verify_imports`, which parses regular
and delay-load imports of the staged DLL through
[`check_pe_imports.py`](tools/check_pe_imports.py) and accepts only its explicit
Windows system DLL allowlist and API-set names. Compiler runtime DLLs must not
be staged. Linux parser tests do not establish a Windows DLL's import table.

The staged external-driver root is `<build>/driver_ltb`; its binary is
`bin/win64/driver_ltb.dll`, and `build-id.txt` is beside
`driver.vrdrivermanifest`. Generated headers, staged files, DLLs, and build
directories are artifacts, not source inputs, and must not be committed.

The complete portable package is produced by
[`build/package-win-x64.sh`](../../build/package-win-x64.sh), using
`build/native-windows/driver_ltb` by default or `LTB_DRIVER_ROOT` for the exact
Windows CI artifact. It verifies the complete required payload and archive
manifest, identity, links, and hashes; the workflow also requires a clean-source
manifest and exact archived/artifact DLL hash equality. See the
[operations guide](../../docs/internal-drivers.md) and
[Windows verification checklist](../../docs/windows-internal-driver-verification.md)
for registration, loaded identity, and hardware gates.
