# Valve OpenVR SDK artifacts

The generated binding and Windows x64 native library are unmodified artifacts
from Valve's official OpenVR repository, pinned together at OpenVR SDK 2.15.6
commit `0924064316de3effbcd1acf1e309182a2deb1c05`.

| Artifact | Official source | Git blob | SHA-256 |
| --- | --- | --- | --- |
| `headers/openvr_api.cs` | <https://github.com/ValveSoftware/openvr/blob/0924064316de3effbcd1acf1e309182a2deb1c05/headers/openvr_api.cs> | `3050761272a0d4191379700cce47ba2c8c17044b` | `c17e878b7b3b925d1f22ef5382561389c47db8b92019de840705ff5ff28c317a` |
| `bin/win64/openvr_api.dll` | <https://github.com/ValveSoftware/openvr/blob/0924064316de3effbcd1acf1e309182a2deb1c05/bin/win64/openvr_api.dll> | `83b201974728158157be0bf6fc3b43caed34ab11` | `bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a` |
| `LICENSE` | <https://github.com/ValveSoftware/openvr/blob/0924064316de3effbcd1acf1e309182a2deb1c05/LICENSE> | `ee83337d7fcb726d14cc10f7dd2fda6799d8a135` | `f56ff606104d4ef18e617921a75c73ad73b5a1a1d70c69590c29de16919e04ad` |

The 837,272-byte PE32+ x86-64 native library is stored at
`src/Ltb.OpenVr/runtimes/win-x64/native/openvr_api.dll`. The generated C#
binding is compiled into the private `Ltb.OpenVr.Interop` implementation
assembly; public Lighthouse Touch Bridge contracts do not expose Valve types.

`Ltb.OpenVr.csproj` copies the native library to the consumer application's
output and publish root as `openvr_api.dll` for RID-neutral and `win-x64`
builds. This puts it beside `Ltb.App.exe` and the interop assembly, where the
binding's `DllImport("openvr_api")` resolves it without a user-managed `PATH`.
An explicit non-Windows or non-x64 RID does not receive this Windows binary.
The same project copies Valve's 3-clause BSD notice to
`licenses/Valve.OpenVR.LICENSE.txt` for binary distributions.
