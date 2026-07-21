#!/usr/bin/env python3
"""Reject PE imports that are not explicitly allowlisted Windows system DLLs."""

from __future__ import annotations

import argparse
import pathlib
import re
import struct
import sys
from collections.abc import Callable


class PeFormatError(ValueError):
    """Raised when an input is not a structurally valid PE image."""


# These are Windows operating-system components, not redistributable compiler
# runtimes. In particular, vcruntime*.dll, msvcp*.dll, libgcc*.dll,
# libstdc++*.dll, and libwinpthread*.dll are intentionally absent.
WINDOWS_SYSTEM_DLLS = frozenset(
    {
        "advapi32.dll",
        "bcrypt.dll",
        "cabinet.dll",
        "cfgmgr32.dll",
        "combase.dll",
        "comctl32.dll",
        "crypt32.dll",
        "d3d11.dll",
        "d3d12.dll",
        "dcomp.dll",
        "dnsapi.dll",
        "dwmapi.dll",
        "dxgi.dll",
        "gdi32.dll",
        "gdi32full.dll",
        "imm32.dll",
        "iphlpapi.dll",
        "kernel32.dll",
        "kernelbase.dll",
        "msimg32.dll",
        "msvcrt.dll",
        "mswsock.dll",
        "normaliz.dll",
        "ntdll.dll",
        "ole32.dll",
        "oleaut32.dll",
        "powrprof.dll",
        "propsys.dll",
        "rpcrt4.dll",
        "sechost.dll",
        "secur32.dll",
        "setupapi.dll",
        "shcore.dll",
        "shell32.dll",
        "shlwapi.dll",
        "ucrtbase.dll",
        "user32.dll",
        "userenv.dll",
        "uxtheme.dll",
        "version.dll",
        "winhttp.dll",
        "winmm.dll",
        "winspool.drv",
        "ws2_32.dll",
    }
)
WINDOWS_API_SET_PATTERN = re.compile(r"^(?:api|ext)-ms-[a-z0-9_.-]+\.dll$")


def is_windows_system_dll(import_name: str) -> bool:
    """Return whether import_name names a known Windows system component."""
    normalized = import_name.casefold()
    return (
        normalized in WINDOWS_SYSTEM_DLLS
        or WINDOWS_API_SET_PATTERN.fullmatch(normalized) is not None
    )


def _unpack_from(
    data: bytes, format_string: str, offset: int, description: str
) -> tuple[int, ...]:
    size = struct.calcsize(format_string)
    if offset < 0 or offset + size > len(data):
        raise PeFormatError(f"{description} extends beyond the input")
    return struct.unpack_from(format_string, data, offset)


def _read_ascii_name(data: bytes, offset: int, description: str) -> str:
    if offset < 0 or offset >= len(data):
        raise PeFormatError(f"{description} points beyond the input")
    end = data.find(b"\0", offset, min(len(data), offset + 4096))
    if end < 0:
        raise PeFormatError(f"{description} is not NUL-terminated")
    raw_name = data[offset:end]
    if not raw_name:
        raise PeFormatError(f"{description} is empty")
    try:
        name = raw_name.decode("ascii")
    except UnicodeDecodeError as error:
        raise PeFormatError(f"{description} is not ASCII") from error
    if pathlib.PureWindowsPath(name).name != name or "/" in name or "\\" in name:
        raise PeFormatError(f"{description} must be a plain DLL filename")
    return name


def parse_pe_imports_bytes(data: bytes) -> tuple[str, ...]:
    """Parse regular and delay-load DLL imports from a PE image."""
    if len(data) < 64 or data[:2] != b"MZ":
        raise PeFormatError("input does not have an MZ header")

    (pe_offset,) = _unpack_from(data, "<I", 0x3C, "DOS PE-header pointer")
    if pe_offset + 24 > len(data) or data[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise PeFormatError("input does not have a valid PE signature")

    (_, number_of_sections, _, _, _, optional_size, _) = _unpack_from(
        data, "<HHIIIHH", pe_offset + 4, "COFF header"
    )
    optional_offset = pe_offset + 24
    if optional_offset + optional_size > len(data):
        raise PeFormatError("optional header extends beyond the input")
    (optional_magic,) = _unpack_from(
        data, "<H", optional_offset, "optional-header magic"
    )
    if optional_magic == 0x10B:
        minimum_optional_size = 96
        image_base_offset = 28
        image_base_format = "<I"
        directory_count_offset = 92
        directory_offset = 96
    elif optional_magic == 0x20B:
        minimum_optional_size = 112
        image_base_offset = 24
        image_base_format = "<Q"
        directory_count_offset = 108
        directory_offset = 112
    else:
        raise PeFormatError(
            f"unsupported PE optional-header magic 0x{optional_magic:04x}"
        )
    if optional_size < minimum_optional_size:
        raise PeFormatError("optional header is too small for its PE format")

    (image_base,) = _unpack_from(
        data,
        image_base_format,
        optional_offset + image_base_offset,
        "image base",
    )
    (size_of_headers,) = _unpack_from(data, "<I", optional_offset + 60, "SizeOfHeaders")
    (directory_count,) = _unpack_from(
        data,
        "<I",
        optional_offset + directory_count_offset,
        "data-directory count",
    )

    section_offset = optional_offset + optional_size
    sections: list[tuple[int, int, int, int]] = []
    for section_index in range(number_of_sections):
        header_offset = section_offset + section_index * 40
        (_, virtual_size, virtual_address, raw_size, raw_offset) = _unpack_from(
            data, "<8sIIII", header_offset, f"section {section_index} header"
        )
        _unpack_from(
            data, "<16x", header_offset + 24, f"section {section_index} header"
        )
        sections.append((virtual_address, virtual_size, raw_offset, raw_size))

    def rva_to_offset(rva: int, description: str) -> int:
        if rva < size_of_headers:
            if rva >= len(data):
                raise PeFormatError(f"{description} header RVA points beyond the input")
            return rva
        for virtual_address, virtual_size, raw_offset, raw_size in sections:
            span = max(virtual_size, raw_size)
            if virtual_address <= rva < virtual_address + span:
                delta = rva - virtual_address
                if delta >= raw_size:
                    raise PeFormatError(
                        f"{description} points into an uninitialized section"
                    )
                file_offset = raw_offset + delta
                if file_offset >= len(data):
                    raise PeFormatError(f"{description} points beyond the input")
                return file_offset
        raise PeFormatError(f"{description} RVA is not mapped by any section")

    def data_directory(index: int) -> tuple[int, int]:
        if index >= directory_count:
            return (0, 0)
        entry_offset = optional_offset + directory_offset + index * 8
        if entry_offset + 8 > optional_offset + optional_size:
            raise PeFormatError(
                f"data-directory {index} is outside the optional header"
            )
        return _unpack_from(data, "<II", entry_offset, f"data-directory {index}")

    imports: list[str] = []

    def parse_descriptor_table(
        directory_index: int,
        descriptor_size: int,
        name_rva_reader: Callable[[int], int],
        description: str,
    ) -> None:
        directory_rva, directory_size = data_directory(directory_index)
        if directory_rva == 0 and directory_size == 0:
            return
        if directory_rva == 0 or directory_size < descriptor_size:
            raise PeFormatError(f"{description} directory is malformed")
        descriptor_count = directory_size // descriptor_size
        if descriptor_count > 65536:
            raise PeFormatError(f"{description} directory has an unreasonable size")
        for descriptor_index in range(descriptor_count):
            descriptor_rva = directory_rva + descriptor_index * descriptor_size
            descriptor_offset = rva_to_offset(
                descriptor_rva, f"{description} descriptor {descriptor_index}"
            )
            descriptor = data[descriptor_offset : descriptor_offset + descriptor_size]
            if len(descriptor) != descriptor_size:
                raise PeFormatError(
                    f"{description} descriptor {descriptor_index} extends beyond the input"
                )
            if not any(descriptor):
                return
            name_rva = name_rva_reader(descriptor_offset)
            name_offset = rva_to_offset(
                name_rva, f"{description} name {descriptor_index}"
            )
            imports.append(
                _read_ascii_name(
                    data, name_offset, f"{description} name {descriptor_index}"
                )
            )
        raise PeFormatError(f"{description} directory has no terminating descriptor")

    parse_descriptor_table(
        1,
        20,
        lambda offset: _unpack_from(data, "<I", offset + 12, "import name RVA")[0],
        "import",
    )

    def delay_name_rva(offset: int) -> int:
        attributes, name_pointer = _unpack_from(
            data, "<II", offset, "delay-import descriptor"
        )
        if name_pointer == 0:
            raise PeFormatError("delay-import descriptor has no DLL name")
        if attributes & 1:
            return name_pointer
        if name_pointer < image_base:
            raise PeFormatError("delay-import DLL name VA is below the image base")
        return name_pointer - image_base

    parse_descriptor_table(13, 32, delay_name_rva, "delay-import")

    unique_imports: dict[str, str] = {}
    for import_name in imports:
        unique_imports.setdefault(import_name.casefold(), import_name)
    return tuple(unique_imports[key] for key in sorted(unique_imports))


def parse_pe_imports(binary_path: pathlib.Path) -> tuple[str, ...]:
    return parse_pe_imports_bytes(binary_path.read_bytes())


def find_non_system_imports(imports: tuple[str, ...]) -> tuple[str, ...]:
    return tuple(
        import_name
        for import_name in imports
        if not is_windows_system_dll(import_name)
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, type=pathlib.Path)
    arguments = parser.parse_args(argv)

    if not arguments.binary.is_file():
        parser.error(f"binary is not a file: {arguments.binary}")

    try:
        imports = parse_pe_imports(arguments.binary)
        non_system_imports = find_non_system_imports(imports)
    except (OSError, PeFormatError) as error:
        print(
            f"PE import check failed for {arguments.binary}: {error}", file=sys.stderr
        )
        return 1

    if non_system_imports:
        print(
            f"PE import check failed for {arguments.binary}: non-system DLL imports: "
            f"{', '.join(non_system_imports)}",
            file=sys.stderr,
        )
        return 1

    import_summary = ", ".join(imports) if imports else "none"
    print(f"Verified PE imports for {arguments.binary}: {import_summary}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
