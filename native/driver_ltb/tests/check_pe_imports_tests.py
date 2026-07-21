#!/usr/bin/env python3
"""Linux-runnable tests for the PE import packaging gate."""

from __future__ import annotations

import importlib.util
import pathlib
import struct
import subprocess
import sys
import tempfile
import unittest


DRIVER_ROOT = pathlib.Path(__file__).resolve().parents[1]
CHECKER_PATH = DRIVER_ROOT / "tools" / "check_pe_imports.py"
SPEC = importlib.util.spec_from_file_location("check_pe_imports", CHECKER_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load checker module from {CHECKER_PATH}")
CHECKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECKER)


def make_pe(
    imports: tuple[str, ...] = (), delay_imports: tuple[str, ...] = ()
) -> bytes:
    """Create a minimal PE32+ fixture containing import descriptor tables."""
    data = bytearray(0xA00)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 0x84, 0x8664, 1, 0, 0, 0, 0xF0, 0x2022)

    optional_offset = 0x98
    struct.pack_into("<H", data, optional_offset, 0x20B)
    struct.pack_into("<Q", data, optional_offset + 24, 0x140000000)
    struct.pack_into("<I", data, optional_offset + 60, 0x200)
    struct.pack_into("<I", data, optional_offset + 108, 16)

    section_offset = optional_offset + 0xF0
    struct.pack_into(
        "<8sIIIIIIHHI",
        data,
        section_offset,
        b".rdata\0\0",
        0x800,
        0x1000,
        0x800,
        0x200,
        0,
        0,
        0,
        0,
        0x40000040,
    )

    name_offset = 0x600

    def add_name(name: str) -> int:
        nonlocal name_offset
        encoded = name.encode("ascii") + b"\0"
        offset = name_offset
        data[offset : offset + len(encoded)] = encoded
        name_offset += len(encoded)
        return 0x1000 + offset - 0x200

    if imports:
        descriptor_offset = 0x200
        for index, import_name in enumerate(imports):
            struct.pack_into(
                "<IIIII",
                data,
                descriptor_offset + index * 20,
                0,
                0,
                0,
                add_name(import_name),
                0,
            )
        directory_size = (len(imports) + 1) * 20
        struct.pack_into("<II", data, optional_offset + 112 + 8, 0x1000, directory_size)

    if delay_imports:
        descriptor_offset = 0x400
        for index, import_name in enumerate(delay_imports):
            struct.pack_into(
                "<IIIIIIII",
                data,
                descriptor_offset + index * 32,
                1,
                add_name(import_name),
                0,
                0,
                0,
                0,
                0,
                0,
            )
        directory_size = (len(delay_imports) + 1) * 32
        struct.pack_into(
            "<II", data, optional_offset + 112 + 13 * 8, 0x1200, directory_size
        )

    return bytes(data)


class PeImportCheckerTests(unittest.TestCase):
    def test_parser_reads_regular_and_delay_load_imports(self) -> None:
        imports = CHECKER.parse_pe_imports_bytes(
            make_pe(("KERNEL32.dll", "Helper.DLL"), ("Delayed.dll",))
        )
        self.assertEqual(imports, ("Delayed.dll", "Helper.DLL", "KERNEL32.dll"))

    def test_system_allowlist_is_case_insensitive_and_excludes_runtimes(self) -> None:
        self.assertTrue(CHECKER.is_windows_system_dll("KeRnEl32.DlL"))
        self.assertTrue(
            CHECKER.is_windows_system_dll("api-ms-win-core-synch-l1-2-0.dll")
        )
        self.assertFalse(CHECKER.is_windows_system_dll("VCRUNTIME140.dll"))
        self.assertFalse(CHECKER.is_windows_system_dll("libstdc++-6.dll"))

    def test_non_system_import_is_rejected(self) -> None:
        rejected = CHECKER.find_non_system_imports(
            ("ADVAPI32.dll", "libwinpthread-1.dll")
        )
        self.assertEqual(rejected, ("libwinpthread-1.dll",))

    def test_cli_rejects_staged_non_system_regular_imports(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            stage_directory = pathlib.Path(temporary_directory)
            binary_path = stage_directory / "driver_ltb.dll"
            rejected_imports = ("VCRUNTIME140.dll", "libc++.dll", "helper.dll")
            binary_path.write_bytes(make_pe(("KERNEL32.dll", *rejected_imports)))
            for import_name in rejected_imports:
                (stage_directory / import_name).write_bytes(b"staged")
            result = subprocess.run(
                [
                    sys.executable,
                    str(CHECKER_PATH),
                    "--binary",
                    str(binary_path),
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn(
                "non-system DLL imports: helper.dll, libc++.dll, VCRUNTIME140.dll",
                result.stderr,
            )

    def test_cli_rejects_staged_non_system_delay_imports(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            stage_directory = pathlib.Path(temporary_directory)
            binary_path = stage_directory / "driver_ltb.dll"
            rejected_imports = ("VCRUNTIME140.dll", "libc++.dll", "helper.dll")
            binary_path.write_bytes(
                make_pe(("ADVAPI32.dll",), delay_imports=rejected_imports)
            )
            for import_name in rejected_imports:
                (stage_directory / import_name).write_bytes(b"staged")
            result = subprocess.run(
                [
                    sys.executable,
                    str(CHECKER_PATH),
                    "--binary",
                    str(binary_path),
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn(
                "non-system DLL imports: helper.dll, libc++.dll, VCRUNTIME140.dll",
                result.stderr,
            )

    def test_cli_accepts_regular_and_delay_system_imports(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            binary_path = pathlib.Path(temporary_directory) / "driver_ltb.dll"
            binary_path.write_bytes(
                make_pe(
                    ("ADVAPI32.dll", "api-ms-win-core-synch-l1-2-0.dll"),
                    ("KERNEL32.dll", "ext-ms-win-ntuser-window-l1-1-0.dll"),
                )
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(CHECKER_PATH),
                    "--binary",
                    str(binary_path),
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("Verified PE imports", result.stdout)

    def test_text_smoke_fixture_is_rejected_as_non_pe(self) -> None:
        fixture_path = pathlib.Path(__file__).parent / "fixtures" / "not-a-pe.txt"
        with tempfile.TemporaryDirectory() as temporary_directory:
            result = subprocess.run(
                [
                    sys.executable,
                    str(CHECKER_PATH),
                    "--binary",
                    str(fixture_path),
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn("input does not have an MZ header", result.stderr)


if __name__ == "__main__":
    unittest.main()
