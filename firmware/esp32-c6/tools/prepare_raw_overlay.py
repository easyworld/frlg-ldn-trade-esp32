"""Build an audited C6 raw-TX adapter without changing the installed SDK."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile

from elftools.elf.elffile import ELFFile

ARCHIVE_SHA256 = "6cd630f338006fdb7dde1f895eeceff653b1c97fdc8e6b7daeac20759690edbf"
EXPORTED = {"ldn_vendor_80211_tx", "ldn_stock_frame_check"}
SECTIONS = {".text.esp_wifi_80211_tx", ".text.ieee80211_raw_frame_sanity_check"}


def prepare(archive, output, ar, objcopy):
    archive = archive.resolve()
    if hashlib.sha256(archive.read_bytes()).hexdigest() != ARCHIVE_SHA256:
        raise ValueError("C6 Wi-Fi archive differs from the audited SDK binary")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as temporary:
        subprocess.run([ar, "x", str(archive), "ieee80211_output.o"], cwd=temporary, check=True)
        source = Path(temporary) / "ieee80211_output.o"
        original = source.read_bytes()
        source_elf = ELFFile(io.BytesIO(original))
        expected_code = {name: source_elf.get_section_by_name(name).data() for name in SECTIONS}
        renamed = Path(temporary) / "renamed.o"
        subprocess.run([
            objcopy,
            "--redefine-sym", "esp_wifi_80211_tx=ldn_vendor_80211_tx",
            "--redefine-sym", "ieee80211_raw_frame_sanity_check=ldn_private_frame_check",
            "--redefine-sym", "ieee80211_post_hmac_tx=ldn_private_post_hmac_tx",
            "--add-symbol", "ldn_stock_frame_check=.text.ieee80211_raw_frame_sanity_check:0,global,function",
            str(source), str(renamed),
        ], check=True)
        data = bytearray(renamed.read_bytes())

    elf = ELFFile(io.BytesIO(data))
    if elf.elfclass != 32 or not elf.little_endian or elf["e_machine"] != "EM_RISCV":
        raise ValueError("Expected a 32-bit little-endian RISC-V object")
    symbols = elf.get_section_by_name(".symtab")
    converted = []
    for index, symbol in enumerate(symbols.iter_symbols()):
        if (symbol["st_info"]["bind"] in ("STB_GLOBAL", "STB_WEAK") and
                symbol["st_shndx"] != "SHN_UNDEF" and symbol.name not in EXPORTED):
            # Reuse the original driver's helpers and shared queues, not copied state.
            entry = symbol.entry.copy()
            entry.update(st_shndx="SHN_UNDEF", st_value=0, st_size=0)
            offset = symbols["sh_offset"] + index * symbols["sh_entsize"]
            data[offset:offset + symbols["sh_entsize"]] = elf.structs.Elf_Sym.build(entry)
            converted.append(symbol.name)

    result = ELFFile(io.BytesIO(data))
    result_symbols = result.get_section_by_name(".symtab")
    for name, code in expected_code.items():
        if result.get_section_by_name(name).data() != code:
            raise ValueError("Overlay must not change any driver instruction bytes")
        for relocation in result.get_section_by_name(".rela" + name).iter_relocations():
            symbol = result_symbols.get_symbol(relocation["r_info_sym"])
            section_index = symbol["st_shndx"]
            if isinstance(section_index, int):
                section = result.get_section(section_index)
                if section["sh_flags"] & 1:
                    raise ValueError(f"Unexpected copied mutable state: {symbol.name}")
    exports = {symbol.name for symbol in result_symbols.iter_symbols()
               if symbol["st_info"]["bind"] == "STB_GLOBAL" and symbol["st_shndx"] != "SHN_UNDEF"}
    if exports != EXPORTED or "ldn_private_frame_check" not in converted:
        raise ValueError("Overlay symbol boundary did not match the audited design")
    output.write_bytes(data)
    output.with_suffix(".audit.json").write_text(json.dumps({
        "sdk_archive_sha256": ARCHIVE_SHA256,
        "original_object_sha256": hashlib.sha256(original).hexdigest(),
        "overlay_sha256": hashlib.sha256(data).hexdigest(),
        "unchanged_code_sections": sorted(SECTIONS),
        "exports": sorted(exports), "reuse_original_symbols": converted,
    }, indent=2) + "\n", encoding="utf-8")
    print("C6 private raw-TX overlay prepared; SDK and instruction bytes unchanged.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ar", required=True)
    parser.add_argument("--objcopy", required=True)
    args = parser.parse_args()
    prepare(args.archive, args.output, args.ar, args.objcopy)
