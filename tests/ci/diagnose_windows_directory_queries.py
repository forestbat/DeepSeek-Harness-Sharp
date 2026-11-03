"""Temporary native query comparison, not a sandbox policy or acceptance test."""

import argparse
import ctypes
import json
import os
import re
import subprocess
import time
from collections import deque
from dataclasses import dataclass


class UnicodeString(ctypes.Structure):
    _fields_ = [("length", ctypes.c_uint16), ("maximum", ctypes.c_uint16), ("buffer", ctypes.c_void_p)]


class ObjectAttributes(ctypes.Structure):
    _fields_ = [("length", ctypes.c_uint32), ("root", ctypes.c_void_p), ("name", ctypes.POINTER(UnicodeString)),
                ("attributes", ctypes.c_uint32), ("security", ctypes.c_void_p), ("quality", ctypes.c_void_p)]


class IoStatus(ctypes.Structure):
    _fields_ = [("status", ctypes.c_size_t), ("information", ctypes.c_size_t)]


@dataclass
class Counts:
    files: int = 0
    directories: int = 1
    queries: int = 0
    opens: int = 0
    query_ns: int = 0
    open_ns: int = 0


class NativeQueries:
    DIRECTORY_INFORMATION = 1
    FULL_DIRECTORY_INFORMATION = 2
    NO_MORE_FILES = 0x80000006
    DIRECTORY = 0x10
    REPARSE_POINT = 0x400
    BUFFER_BYTES = 4096
    LIST_DIRECTORY = 0x0001
    SYNCHRONIZE = 0x00100000
    SHARE_ALL = 7
    OPEN_EXISTING = 3
    FILE_OPEN = 1
    CASE_INSENSITIVE = 0x40
    SYNCHRONOUS_NONALERT = 0x20
    DIRECTORY_FILE = 1
    BACKUP_INTENT = 0x4000
    BACKUP_SEMANTICS = 0x02000000
    ATTRIBUTES_OFFSET = 56
    NAME_LENGTH_OFFSET = 60
    DIRECTORY_NAME_OFFSET = 64
    FULL_DIRECTORY_NAME_OFFSET = 68

    def __init__(self):
        if ctypes.sizeof(ctypes.c_void_p) != 8 or ctypes.sizeof(ObjectAttributes) != 48 or ctypes.sizeof(IoStatus) != 16:
            raise RuntimeError("The native query probe requires the validated 64-bit structure layout.")
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.nt = ctypes.WinDLL("ntdll")
        self.kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                                           ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
        self.kernel.CreateFileW.restype = ctypes.c_void_p
        self.kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        self.nt.NtCreateFile.argtypes = [ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint32, ctypes.POINTER(ObjectAttributes),
                                        ctypes.POINTER(IoStatus), ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32,
                                        ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32]
        self.nt.NtCreateFile.restype = ctypes.c_int32
        self.nt.NtQueryDirectoryFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                                ctypes.POINTER(IoStatus), ctypes.c_void_p, ctypes.c_uint32,
                                                ctypes.c_uint32, ctypes.c_uint8, ctypes.c_void_p, ctypes.c_uint8]
        self.nt.NtQueryDirectoryFile.restype = ctypes.c_int32

    def open_child(self, parent, name, counts):
        encoded = name.encode("utf-16-le")
        buffer = ctypes.create_string_buffer(encoded + b"\0\0")
        unicode = UnicodeString(len(encoded), len(encoded) + 2, ctypes.addressof(buffer))
        attributes = ObjectAttributes(ctypes.sizeof(ObjectAttributes), parent, ctypes.pointer(unicode), self.CASE_INSENSITIVE, None, None)
        handle, io = ctypes.c_void_p(), IoStatus()
        start = time.perf_counter_ns()
        status = self.nt.NtCreateFile(ctypes.byref(handle), self.LIST_DIRECTORY | self.SYNCHRONIZE, ctypes.byref(attributes), ctypes.byref(io),
                                      None, 0, self.SHARE_ALL, self.FILE_OPEN, self.SYNCHRONOUS_NONALERT | self.DIRECTORY_FILE | self.BACKUP_INTENT, None, 0)
        counts.open_ns += time.perf_counter_ns() - start
        counts.opens += 1
        if status < 0:
            raise RuntimeError(f"Native child open failed status={status & 0xffffffff}")
        return handle.value

    def read_directory(self, handle, information, buffer, counts, pending):
        io = IoStatus()
        name_offset = self.DIRECTORY_NAME_OFFSET if information == self.DIRECTORY_INFORMATION else self.FULL_DIRECTORY_NAME_OFFSET
        while True:
            start = time.perf_counter_ns()
            status = self.nt.NtQueryDirectoryFile(handle, None, None, None, ctypes.byref(io), buffer,
                                                  self.BUFFER_BYTES, information, 0, None, 0)
            counts.query_ns += time.perf_counter_ns() - start
            counts.queries += 1
            if status & 0xffffffff == self.NO_MORE_FILES:
                return
            if status != 0:
                raise RuntimeError(f"Native directory query failed status={status & 0xffffffff}")
            self.read_entries(handle, buffer, io.information, name_offset, counts, pending)

    def read_entries(self, handle, buffer, used, name_offset, counts, pending):
        offset = 0
        while True:
            if offset + name_offset > used:
                raise RuntimeError("Native directory record header exceeds the returned buffer.")
            entry = ctypes.addressof(buffer) + offset
            next_offset = ctypes.c_uint32.from_address(entry).value
            attributes = ctypes.c_uint32.from_address(entry + self.ATTRIBUTES_OFFSET).value
            length = ctypes.c_uint32.from_address(entry + self.NAME_LENGTH_OFFSET).value
            if length % 2 or offset + name_offset + length > used:
                raise RuntimeError("Native directory name exceeds the returned buffer.")
            if not attributes & self.REPARSE_POINT:
                if attributes & self.DIRECTORY:
                    name = ctypes.string_at(entry + name_offset, length).decode("utf-16-le")
                    if name not in (".", ".."):
                        pending.append(self.open_child(handle, name, counts))
                        counts.directories += 1
                else:
                    counts.files += 1
            if next_offset == 0:
                return
            offset += next_offset

    def walk(self, root, information):
        counts = Counts()
        start = time.perf_counter_ns()
        handle = self.kernel.CreateFileW(root, self.LIST_DIRECTORY, self.SHARE_ALL, None, self.OPEN_EXISTING, self.BACKUP_SEMANTICS, None)
        if handle == ctypes.c_void_p(-1).value:
            raise RuntimeError(f"Native root open failed error={ctypes.get_last_error()}")
        pending = deque([handle])
        buffer = ctypes.create_string_buffer(self.BUFFER_BYTES)
        try:
            while pending:
                handle = pending.popleft()
                try:
                    self.read_directory(handle, information, buffer, counts, pending)
                finally:
                    self.kernel.CloseHandle(handle)
        finally:
            for handle in pending:
                self.kernel.CloseHandle(handle)
        return {**vars(counts), "wall_ns": time.perf_counter_ns() - start}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--order", choices=("full-first", "directory-first"), required=True)
    options = parser.parse_args()
    if os.name != "nt":
        parser.error("Native Windows is required; no emulated result is available.")
    inventory = subprocess.run(["dotnet", "--list-sdks"], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding="utf-8", errors="strict", timeout=15)
    if inventory.returncode != 0:
        raise RuntimeError("The installed SDK inventory failed.")
    roots = list(dict.fromkeys(re.findall(r"\[([^\r\n]+)\]", inventory.stdout)))
    if not roots:
        raise RuntimeError("No actual SDK root was reported.")
    native = NativeQueries()
    variants = [("full", native.FULL_DIRECTORY_INFORMATION), ("directory", native.DIRECTORY_INFORMATION)]
    if options.order == "directory-first":
        variants.reverse()
    for index, root in enumerate(roots):
        results = []
        for label, information in variants:
            row = {"root_index": index, "variant": label, **native.walk(root, information)}
            results.append(row)
            print(f"WINDOWS_DIRECTORY_QUERY {json.dumps(row, separators=(',', ':'))}", flush=True)
        equal = all(results[0][key] == results[1][key] for key in ("files", "directories"))
        print(f"WINDOWS_DIRECTORY_QUERY counts_equal={equal}", flush=True)
        if not equal:
            raise RuntimeError("The two native queries did not enumerate the same live entry counts.")


if __name__ == "__main__":
    main()
