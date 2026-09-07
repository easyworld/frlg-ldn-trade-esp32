"""Probe a disconnected Windows WLAN adapter's native monitor mode, then restore it."""

import argparse
import ctypes as ct
from ctypes import wintypes as wt
import sys


class GUID(ct.Structure):
    _fields_ = [("data", ct.c_ubyte * 16)]


class Interface(ct.Structure):
    _fields_ = [("guid", GUID), ("description", wt.WCHAR * 256), ("state", wt.DWORD)]


def check(result, operation):
    if result:
        raise OSError(result, f"{operation}: {ct.FormatError(result).strip()}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe-mode", action="store_true")
    parser.add_argument("--adapter", help="Exact adapter description to probe")
    args = parser.parse_args()
    if sys.platform != "win32":
        parser.error("Windows is required")
    if args.probe_mode and not args.adapter:
        parser.error("--probe-mode requires an exact --adapter description")

    api = ct.WinDLL("wlanapi")
    api.WlanOpenHandle.argtypes = [wt.DWORD, ct.c_void_p, ct.POINTER(wt.DWORD), ct.POINTER(wt.HANDLE)]
    api.WlanEnumInterfaces.argtypes = [wt.HANDLE, ct.c_void_p, ct.POINTER(ct.c_void_p)]
    api.WlanQueryInterface.argtypes = [wt.HANDLE, ct.POINTER(GUID), ct.c_int, ct.c_void_p,
                                      ct.POINTER(wt.DWORD), ct.POINTER(ct.c_void_p), ct.POINTER(ct.c_int)]
    api.WlanSetInterface.argtypes = [wt.HANDLE, ct.POINTER(GUID), ct.c_int, wt.DWORD,
                                    ct.c_void_p, ct.c_void_p]
    api.WlanFreeMemory.argtypes = [ct.c_void_p]
    api.WlanCloseHandle.argtypes = [wt.HANDLE, ct.c_void_p]
    for name in ("WlanOpenHandle", "WlanEnumInterfaces", "WlanQueryInterface",
                 "WlanSetInterface", "WlanCloseHandle"):
        getattr(api, name).restype = wt.DWORD
    api.WlanFreeMemory.restype = None
    handle, version = wt.HANDLE(), wt.DWORD()
    check(api.WlanOpenHandle(2, None, ct.byref(version), ct.byref(handle)), "WlanOpenHandle")
    entries = ct.c_void_p()
    try:
        check(api.WlanEnumInterfaces(handle, None, ct.byref(entries)), "WlanEnumInterfaces")
        count = ct.cast(entries, ct.POINTER(wt.DWORD))[0]
        interfaces = ct.cast(entries.value + 8, ct.POINTER(Interface))
        matched = False
        for i in range(count):
            interface = interfaces[i]
            print(f"Adapter: {interface.description}; state={interface.state}", flush=True)
            if not args.probe_mode or interface.description != args.adapter:
                continue
            matched = True
            if interface.state != 4:
                raise RuntimeError("Monitor probe requires a disconnected adapter")
            size, value, opcode_type = wt.DWORD(), ct.c_void_p(), ct.c_int()
            check(api.WlanQueryInterface(handle, ct.byref(interface.guid), 12, None,
                                         ct.byref(size), ct.byref(value), ct.byref(opcode_type)),
                  "Query current operation mode")
            try:
                if size.value != ct.sizeof(wt.DWORD):
                    raise RuntimeError("Unexpected operation mode size")
                original = wt.DWORD(ct.cast(value, ct.POINTER(wt.DWORD))[0])
            finally:
                api.WlanFreeMemory(value)
            print(f"Current operation mode: 0x{original.value:08x}", flush=True)
            monitor = wt.DWORD(0x80000000)
            result = api.WlanSetInterface(handle, ct.byref(interface.guid), 12,
                                          ct.sizeof(monitor), ct.byref(monitor), None)
            if result:
                print(f"Monitor mode unavailable: error={result} ({ct.FormatError(result).strip()})")
                return 2
            try:
                print("Native monitor mode accepted by the driver.", flush=True)
            finally:
                check(api.WlanSetInterface(handle, ct.byref(interface.guid), 12,
                                            ct.sizeof(original), ct.byref(original), None),
                      "Restore original operation mode")
                print("Original operation mode restored.", flush=True)
        if args.probe_mode and not matched:
            raise ValueError("Specified adapter was not found")
    finally:
        if entries:
            api.WlanFreeMemory(entries)
        api.WlanCloseHandle(handle, None)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
