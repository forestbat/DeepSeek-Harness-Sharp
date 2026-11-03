"""Temporary native query comparison, not a sandbox policy or acceptance test."""

import argparse
import ctypes
import hashlib
import json
import os
import re
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from queue import Empty, Full, Queue
from threading import Event, Lock
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
    directories: int = 0
    queries: int = 0
    opens: int = 0
    query_ns: int = 0
    open_ns: int = 0
    close_ns: int = 0


class NativeQueries:
    WORKERS = 4
    PENDING_LIMIT = 64
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
        self.handle_lock = Lock()
        self.live_handles = 0
        self.peak_handles = 0

    def acquired(self):
        with self.handle_lock:
            self.live_handles += 1
            self.peak_handles = max(self.peak_handles, self.live_handles)

    def close(self, handle, counts):
        start = time.perf_counter_ns()
        closed = self.kernel.CloseHandle(handle)
        counts.close_ns += time.perf_counter_ns() - start
        if not closed:
            raise RuntimeError(f"Native close failed error={ctypes.get_last_error()}")
        with self.handle_lock:
            self.live_handles -= 1

    def open_child(self, parent, name, counts):
        encoded = name.encode("utf-16-le", "surrogatepass")
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
        self.acquired()
        return handle.value

    def read_directory(self, handle, buffer, counts, entries, relative, enqueue, stopped):
        io = IoStatus()
        while not stopped.is_set():
            start = time.perf_counter_ns()
            status = self.nt.NtQueryDirectoryFile(handle, None, None, None, ctypes.byref(io), buffer,
                                                  self.BUFFER_BYTES, self.FULL_DIRECTORY_INFORMATION, 0, None, 0)
            counts.query_ns += time.perf_counter_ns() - start
            counts.queries += 1
            if status & 0xffffffff == self.NO_MORE_FILES:
                return
            if status != 0:
                raise RuntimeError(f"Native directory query failed status={status & 0xffffffff}")
            self.read_entries(handle, buffer, io.information, counts, entries, relative, enqueue, stopped)

    def read_entries(self, handle, buffer, used, counts, entries, relative, enqueue, stopped):
        offset = 0
        name_offset = self.FULL_DIRECTORY_NAME_OFFSET
        while not stopped.is_set():
            if offset + name_offset > used:
                raise RuntimeError("Native directory record header exceeds the returned buffer.")
            entry = ctypes.addressof(buffer) + offset
            next_offset = ctypes.c_uint32.from_address(entry).value
            attributes = ctypes.c_uint32.from_address(entry + self.ATTRIBUTES_OFFSET).value
            length = ctypes.c_uint32.from_address(entry + self.NAME_LENGTH_OFFSET).value
            if length % 2 or offset + name_offset + length > used:
                raise RuntimeError("Native directory name exceeds the returned buffer.")
            if not attributes & self.REPARSE_POINT:
                name = ctypes.string_at(entry + name_offset, length).decode("utf-16-le", "surrogatepass")
                if name not in (".", ".."):
                    path = f"{relative}/{name}"
                    if path in entries:
                        raise RuntimeError("Native enumeration returned a duplicate relative path.")
                    entries[path] = attributes
                    if attributes & self.DIRECTORY:
                        child = self.open_child(handle, name, counts)
                        enqueue(child, path)
                        counts.directories += 1
                    else:
                        counts.files += 1
            if next_offset == 0:
                return
            offset += next_offset

    def walk(self, root, workers):
        if self.live_handles:
            raise RuntimeError("The previous native walk retained an owned handle.")
        self.peak_handles = 0
        start = time.perf_counter_ns()
        handle = self.kernel.CreateFileW(root, self.LIST_DIRECTORY, self.SHARE_ALL, None, self.OPEN_EXISTING, self.BACKUP_SEMANTICS, None)
        if handle == ctypes.c_void_p(-1).value:
            raise RuntimeError(f"Native root open failed error={ctypes.get_last_error()}")
        self.acquired()
        pending = Queue(maxsize=self.PENDING_LIMIT)
        pending.put((handle, ""))
        stopped, errors = Event(), Queue()

        def worker():
            counts, entries = Counts(), {}

            def process(current, relative):
                try:
                    if not stopped.is_set():
                        buffer = ctypes.create_string_buffer(self.BUFFER_BYTES)
                        self.read_directory(current, buffer, counts, entries, relative, enqueue, stopped)
                except BaseException as error:
                    errors.put(error)
                    stopped.set()
                finally:
                    self.close(current, counts)

            def enqueue(child, relative):
                try:
                    pending.put_nowait((child, relative))
                except Full:
                    # Caller-runs keeps a full queue from blocking every directory owner.
                    process(child, relative)
                except BaseException:
                    self.close(child, counts)
                    raise

            while True:
                item = pending.get()
                try:
                    if item is None:
                        return counts, entries
                    process(*item)
                except BaseException as error:
                    errors.put(error)
                    stopped.set()
                finally:
                    pending.task_done()

        with ThreadPoolExecutor(max_workers=workers) as pool:
            futures = [pool.submit(worker) for _ in range(workers)]
            try:
                pending.join()
            finally:
                stopped.set()
                for _ in futures:
                    pending.put(None)
                results = [future.result() for future in futures]
        try:
            error = errors.get_nowait()
        except Empty:
            error = None
        if error is not None:
            raise error
        if self.live_handles:
            raise RuntimeError("The native walk retained an owned handle.")
        total, entries = Counts(directories=1), {}
        for counts, found in results:
            for field in vars(counts):
                setattr(total, field, getattr(total, field) + getattr(counts, field))
            if entries.keys() & found.keys():
                raise RuntimeError("Native worker partitions contain duplicate relative paths.")
            entries.update(found)
        digest = hashlib.sha256()
        for path, attributes in sorted(entries.items()):
            encoded = path.encode("utf-16-le", "surrogatepass")
            digest.update(len(encoded).to_bytes(4, "little"))
            digest.update(encoded)
            digest.update(attributes.to_bytes(4, "little"))
        return {**vars(total), "workers": workers, "pending_limit": self.PENDING_LIMIT, "peak_handles": self.peak_handles,
                "path_attributes_sha256": digest.hexdigest(), "wall_ns": time.perf_counter_ns() - start}, entries


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--order", choices=("parallel-first", "sequential-first"), required=True)
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
    variants = [("parallel", native.WORKERS), ("sequential", 1)]
    if options.order == "sequential-first":
        variants.reverse()
    for index, root in enumerate(roots):
        snapshots = []
        for label, workers in variants:
            result, snapshot = native.walk(root, workers)
            row = {"root_index": index, "variant": label, **result}
            snapshots.append(snapshot)
            print(f"WINDOWS_DIRECTORY_QUERY {json.dumps(row, separators=(',', ':'))}", flush=True)
        equal = snapshots[0] == snapshots[1]
        print(f"WINDOWS_DIRECTORY_QUERY paths_and_attributes_equal={equal}", flush=True)
        if not equal:
            raise RuntimeError("The two native walks did not enumerate identical relative paths and attributes.")


if __name__ == "__main__":
    main()
