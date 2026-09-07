"""Verify the linked private raw path and that driver queue state is shared."""

import argparse
from pathlib import Path
import re
import subprocess

from elftools.elf.elffile import ELFFile


def audit(path, objdump):
    for function, destination in (
        ("ldn_vendor_80211_tx", "ldn_private_frame_check"),
        ("ldn_private_frame_check", "ldn_stock_frame_check"),
        ("ldn_vendor_80211_tx", "ldn_private_post_hmac_tx"),
        ("ldn_private_post_hmac_tx", "ieee80211_post_hmac_tx"),
        ("esp_wifi_80211_tx", "ieee80211_raw_frame_sanity_check"),
        ("lmacSetTxFrame", "__wrap_hal_mac_tx_set_ppdu"),
        ("__wrap_hal_mac_tx_set_ppdu", "hal_mac_tx_set_ppdu"),
    ):
        assembly = subprocess.check_output(
            [objdump, "-d", "--disassemble=" + function, str(path)], text=True
        )
        if not re.search(r"\b(?:jal|jalr|j|jr)\b[^\n]*<" + re.escape(destination) + r">", assembly):
            raise ValueError(f"Linked call missing: {function} -> {destination}")
    with path.open("rb") as stream:
        elf = ELFFile(stream)
        symbols = elf.get_section_by_name(".symtab")
        queues = [symbol for symbol in symbols.iter_symbols()
                  if symbol.name == "s_tx_cacheq" and symbol["st_shndx"] != "SHN_UNDEF"]
        if len(queues) != 1:
            raise ValueError("Private path must reuse exactly one driver TX queue")
    print("Private raw link verified: scoped adapter, stock checks, shared driver queue.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("elf", type=Path)
    parser.add_argument("--objdump", required=True)
    args = parser.parse_args()
    audit(args.elf, args.objdump)
