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
| 128 | 4 | battery `[0,1]`, or zero when absent |

The heartbeat ends after offset 47 and has payload length 32. The hand state
ends after offset 131 and has payload length 116. Flag bits 0 through 7 are,
in order: connected, orientation valid, position valid, linear velocity valid,
angular velocity valid, inputs valid, battery present, and tracked. Unknown
flags and input bits are rejected.
Touch bits 0 through 6 are primary, secondary, trigger, stick, thumbrest,
index-pointing, and thumb-up states.
Button bits 0 through 4 are primary, secondary, menu, thumbstick click, and
trigger click.
The wire-level menu bit is the public LibOVR left Menu/Enter input. The native
left controller maps it exclusively to SteamVR's reserved
`/input/system/click` component so it summons or dismisses the SteamVR
dashboard. The right controller creates no system component. Reserved system
input is declared only as a left-side source in the LTB input profile so
SteamVR can resolve it consistently, but it is absent from application
bindings and automatic remapping; the bundled VRChat binding keeps its
application Quick Menu on Y/B.
When either velocity-valid flag is clear, its three velocity fields must be
exactly zero. A disconnected state may retain only the battery-present flag;
all tracking and input-valid flags must be clear.

The Windows pipe name is `\\.\pipe\lighthouse-touch-bridge-v1`, matching the
managed `NamedPipeDriverTransportFactory` default.

The staged driver has one deterministic build identity in the format
`driver_ltb-<major>.<minor>.<patch>-ipc-<major>.<minor>`. CMake derives it from
the stable driver project version and IPC major/minor, then generates both the
compile-time constant published through OpenVR's `Prop_DriverVersion_String`
and the staged `build-id.txt`. The marker and runtime property therefore name
the same build without using a timestamp, source path, or machine state.

Sequence numbers increase globally within a session. Timestamp monotonicity is
checked independently for heartbeats, left-hand samples, and right-hand
samples: a heartbeat carries producer send time, while a hand packet carries
that hand's mapped pose-sample time. Consequently, a hand sample may be older
than a preceding heartbeat or the other hand's sample. A new, non-retired
session starts at sequence zero and has no timestamp relationship to the
retired session. Watchdog freshness uses local arrival time, not producer time.

The final pose is already composed in OpenVR raw/driver space. The shell keeps
`qWorldFromDriver` and its translation at identity. A client-space Standing
pose must not be sent over this protocol.

## Build and test

Linux builds only the portable core:

```bash
cmake -S native/driver_ltb -B build/native -G Ninja \
  -DLTB_BUILD_OPENVR_DRIVER=OFF -DLTB_BUILD_TESTS=ON
cmake --build build/native
ctest --test-dir build/native --output-on-failure
```

On Windows with Visual Studio 2022, configure with `-A x64`. The staged
external-driver root is `<build>/driver_ltb`; its binary is
`bin/win64/driver_ltb.dll`, and `build-id.txt` is beside
`driver.vrdrivermanifest`. Generated headers, staged files, DLLs, and build
directories are artifacts, not source inputs, and must not be committed.

Windows builds statically link the compiler runtime: CMake selects `/MT` for
MSVC, `-static` for LLVM-MinGW, and `-static -static-libgcc -static-libstdc++`
for MinGW GCC. The staging build then runs `tools/check_pe_imports.py` against
`driver_ltb.dll`. The checker parses regular and delay-load PE imports and
rejects every non-system DLL, even if a same-named file is staged beside the
driver. Compiler runtime DLLs are deliberately not treated as Windows system
components.
