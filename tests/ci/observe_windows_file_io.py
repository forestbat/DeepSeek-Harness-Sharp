"""Temporary owned, bounded-memory kernel I/O capture for one real consumer."""

import ctypes
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid


class NativeFileIoCapture:
    def __init__(self, emit):
        self.emit = emit
        self.instance = "DshOwnedIoR14_" + uuid.uuid4().hex
        self.directory = None
        self.owned = False
        self.cancel_attempted = False
        self.wpr = None
        self.control_unknown = False

    @staticmethod
    def sessions():
        class Header(ctypes.Structure):
            _fields_ = [("BufferSize", ctypes.c_uint32), ("ProviderId", ctypes.c_uint32),
                        ("HistoricalContext", ctypes.c_uint64), ("TimeStamp", ctypes.c_int64),
                        ("Guid", ctypes.c_ubyte * 16), ("ClientContext", ctypes.c_uint32), ("Flags", ctypes.c_uint32)]

        class Properties(ctypes.Structure):
            _fields_ = [("Wnode", Header)] + [(name, ctypes.c_uint32) for name in (
                "BufferSize", "MinimumBuffers", "MaximumBuffers", "MaximumFileSize", "LogFileMode", "FlushTimer", "EnableFlags", "AgeLimit",
                "NumberOfBuffers", "FreeBuffers", "EventsLost", "BuffersWritten", "LogBuffersLost", "RealTimeBuffersLost")]
            _fields_ += [("LoggerThreadId", ctypes.c_void_p), ("LogFileNameOffset", ctypes.c_uint32), ("LoggerNameOffset", ctypes.c_uint32)]

        capacity = 64
        while capacity <= 1024:
            size = ctypes.sizeof(Properties)
            buffers = [ctypes.create_string_buffer(size + 4096) for _ in range(capacity)]
            pointers = (ctypes.c_void_p * capacity)()
            for index, buffer in enumerate(buffers):
                properties = Properties.from_buffer(buffer)
                properties.Wnode.BufferSize = len(buffer)
                properties.LoggerNameOffset = size
                properties.LogFileNameOffset = size + 2048
                pointers[index] = ctypes.addressof(buffer)
            count = ctypes.c_uint32()
            query = ctypes.WinDLL("advapi32").QueryAllTracesW
            query.argtypes = [ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
            query.restype = ctypes.c_uint32
            code = query(pointers, capacity, ctypes.byref(count))
            if code == 234:
                capacity = max(capacity * 2, count.value)
                continue
            if code != 0 or count.value > capacity:
                raise OSError("ETW enumeration unavailable")
            rows = []
            for buffer in buffers[:count.value]:
                properties = Properties.from_buffer(buffer)
                offset = properties.LoggerNameOffset
                if offset < size or offset + 2048 > len(buffer):
                    raise OSError("ETW metadata incomplete")
                name = ctypes.wstring_at(ctypes.addressof(buffer) + offset, 1024).split("\0", 1)[0]
                rows.append((name, properties.BufferSize, properties.MaximumBuffers, properties.NumberOfBuffers))
            return rows
        raise OSError("ETW enumeration exceeds bound")

    def command(self, arguments, timeout):
        return subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)

    def start(self):
        if os.name != "nt":
            self.emit("NATIVE_IO_UNAVAILABLE code=10")
            return
        try:
            self.wpr = shutil.which("wpr")
            if self.wpr is None or any(row[0].startswith("WPR_initiated_") for row in self.sessions()):
                self.emit("NATIVE_IO_UNAVAILABLE code=11")
                return
            parent = Path(".artifacts/native-startup-io").resolve()
            parent.mkdir(parents=True, exist_ok=True)
            self.directory = tempfile.TemporaryDirectory(prefix="owned-", dir=parent)
            profile = Path(self.directory.name) / "owned.wprp"
            profile.write_text(PROFILE, encoding="utf-8")
            self.control_unknown = True
            result = self.command([self.wpr, "-start", f"{profile}!DshOwnedIo.Light", "-filemode", "-recordtempto", self.directory.name, "-instancename", self.instance], 15)
            self.owned = result.returncode == 0
            if self.owned:
                self.control_unknown = False
            rows = [row for row in self.sessions() if row[0].startswith("WPR_initiated_") and self.instance in row[0]]
            if result.returncode != 0:
                self.owned = bool(rows)
                self.control_unknown = False
                self.emit(f"NATIVE_IO_UNAVAILABLE code=12 exit={result.returncode}")
                self.cancel()
                return
            if len(rows) != 1 or rows[0][1] <= 0 or max(rows[0][2], rows[0][3]) <= 0 or rows[0][1] * max(rows[0][2], rows[0][3]) > 128 * 1024:
                self.emit("NATIVE_IO_UNAVAILABLE code=13")
                self.cancel()
                return
            self.emit(f"NATIVE_IO_STARTED collectors={len(rows)} buffer_kb={rows[0][1]} maximum_buffers={rows[0][2]} actual_buffers={rows[0][3]} file_mode=1 maximum_file_mb=512")
        except Exception:
            self.emit("NATIVE_IO_UNAVAILABLE code=14")
            if self.control_unknown and not self.owned:
                try:
                    self.owned = any(self.instance in row[0] and row[0].startswith("WPR_initiated_") for row in self.sessions())
                    self.control_unknown = False
                except Exception:
                    self.emit("NATIVE_IO_UNAVAILABLE code=19")
            self.cancel()

    def cancel(self):
        if self.owned and not self.cancel_attempted:
            self.cancel_attempted = True
            try:
                result = self.command([self.wpr, "-cancel", "-instancename", self.instance], 15)
                self.emit(f"NATIVE_IO_CANCEL exit={result.returncode}")
                if result.returncode == 0:
                    self.owned = False
            except Exception:
                self.emit("NATIVE_IO_UNAVAILABLE code=15")

    def finish(self, pid):
        try:
            if not self.owned:
                return
            etl = Path(self.directory.name) / "owned.etl"
            stopped = self.command([self.wpr, "-stop", str(etl), "-skipPdbGen", "-instancename", self.instance], 90)
            if stopped.returncode != 0:
                self.emit(f"NATIVE_IO_UNAVAILABLE code=16 exit={stopped.returncode}")
                return
            self.owned = False
            self.emit(f"NATIVE_IO_FILE bytes={etl.stat().st_size}")
            result = self.command(["dotnet", "run", "--project", "tests/ci/NativeIoSummary/NativeIoSummary.csproj", "--", str(etl), str(pid)], 120)
            lines = result.stdout.decode("utf-8", errors="replace").splitlines()
            emitted = 0
            for line in lines:
                if re.fullmatch(r"NATIVE_IO_[A-Z_]+(?: [a-z][a-z0-9_]*=-?[0-9]+(?:\.[0-9]+)?)*", line):
                    self.emit(line)
                    emitted += 1
            if result.returncode != 0 or emitted == 0:
                self.emit(f"NATIVE_IO_UNAVAILABLE code=17 exit={result.returncode}")
        except Exception:
            self.emit("NATIVE_IO_UNAVAILABLE code=18")
        finally:
            self.cancel()
            if self.directory is not None:
                try:
                    self.directory.cleanup()
                    self.emit(f"NATIVE_IO_CLEANUP raw_removed=1 trace_stopped={int(not self.owned and not self.control_unknown)}")
                except Exception:
                    self.emit(f"NATIVE_IO_CLEANUP raw_removed=0 trace_stopped={int(not self.owned and not self.control_unknown)}")


PROFILE = """<?xml version="1.0" encoding="utf-8"?>
<WindowsPerformanceRecorder Version="1.0">
  <Profiles>
    <SystemCollector Id="OwnedSystem" Name="NT Kernel Logger">
      <BufferSize Value="128"/><Buffers Value="1024"/>
      <MaximumFileSize Value="512" FileMode="Sequential"/>
    </SystemCollector>
    <SystemProvider Id="OwnedIo">
      <Keywords>
        <Keyword Value="ProcessThread"/><Keyword Value="FileIO"/><Keyword Value="FileIOInit"/>
        <Keyword Value="DiskIO"/><Keyword Value="DiskIOInit"/>
      </Keywords>
    </SystemProvider>
    <Profile Id="DshOwnedIo.Light.Memory" Name="DshOwnedIo" Description="Bounded owned native I/O" DetailLevel="Light" LoggingMode="Memory">
      <ProblemCategories><ProblemCategory Value="First Level Triage"/></ProblemCategories>
      <Collectors><SystemCollectorId Value="OwnedSystem"><SystemProviderId Value="OwnedIo"/></SystemCollectorId></Collectors>
    </Profile>
    <Profile Id="DshOwnedIo.Light.File" Name="DshOwnedIo" Description="Bounded owned native I/O" Base="DshOwnedIo.Light.Memory" DetailLevel="Light" LoggingMode="File"/>
  </Profiles>
</WindowsPerformanceRecorder>
"""
