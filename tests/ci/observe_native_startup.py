"""Temporary bounded stack capture for actual native startup consumers."""

import argparse
import json
import os
import re
import subprocess
import sys
import threading


class NativeStartupObserver:
    def __init__(self, options):
        self.options = options
        self.finished = threading.Event()
        self.output_lock = threading.Lock()
        self.state_lock = threading.Lock()
        self.samplers = []
        self.calls = {}
        self.marker_seen = False
        self.stack_reports = 0
        self.marker_completed = None
        self.marker = re.compile(options.marker.encode("utf-8"))
        self.marker_call = None
        self.process = None

    def emit(self, text):
        with self.output_lock:
            sys.stdout.buffer.write(text.encode("utf-8") + b"\n")
            sys.stdout.buffer.flush()

    def owner_identity(self, pid):
        if pid == self.process.pid:
            return (pid,) if self.process.poll() is None else None
        if os.name != "nt" or self.process.poll() is not None:
            return None
        command = f"Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}' | Select-Object ProcessId,ParentProcessId,CreationDate | ConvertTo-Json -Compress"
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-NonInteractive", "-Command", command],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            encoding="utf-8", errors="replace", timeout=5,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        if result.returncode != 0 or not result.stdout.strip():
            return None
        row = json.loads(result.stdout)
        if not isinstance(row, dict) or not row.get("CreationDate"):
            return None
        if row.get("ProcessId") != pid or row.get("ParentProcessId") != self.process.pid:
            return None
        return (pid, row.get("ParentProcessId"), row.get("CreationDate"))

    def collect(self, label, pid, completed):
        if completed.wait(self.options.sample_after) or self.finished.is_set():
            return
        with self.state_lock:
            if self.stack_reports >= 4:
                return
            self.stack_reports += 1
        self.emit(f"NATIVE_STACK_BEGIN kind={label} pid={pid}")
        try:
            identity = self.owner_identity(pid)
            if identity is None or completed.is_set() or self.finished.is_set():
                self.emit("NATIVE_STACK_UNVERIFIED_OWNER")
                return
            report = subprocess.run(
                [self.options.stack_tool, "report", "--process-id", str(pid)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, encoding="utf-8", errors="replace", timeout=10,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
            self.emit(f"NATIVE_STACK_RESULT exit={report.returncode}")
            if self.owner_identity(pid) != identity or completed.is_set():
                self.emit("NATIVE_STACK_OWNER_ENDED_OR_OPERATION_COMPLETED")
                return
            self.emit_frames(report.stdout)
        except subprocess.TimeoutExpired:
            self.emit("NATIVE_STACK_TIMEOUT")
        except (OSError, ValueError):
            self.emit("NATIVE_STACK_UNAVAILABLE")

    def emit_frames(self, output):
        count = 0
        for line in output.splitlines():
            frame = line.strip()
            if re.fullmatch(r"Thread \([0-9a-fA-Fx]+\):?", frame):
                self.emit(f"NATIVE_STACK {frame}")
            elif frame == "[Native Frames]" or ("(" in frame and frame.endswith(")") and re.fullmatch(r"[\w\s.+`<>!,()\[\]&*?-]+", frame)):
                self.emit(f"NATIVE_STACK {frame}")
                count += 1
        self.emit(f"NATIVE_STACK_END frames={count}")

    def schedule(self, label, pid):
        completed = threading.Event()
        sampler = threading.Thread(target=self.collect, args=(label, pid, completed), daemon=True)
        self.samplers.append(sampler)
        sampler.start()
        return completed

    def observe(self, line):
        with self.state_lock:
            if self.finished.is_set():
                return
            if self.marker.match(line) and not self.marker_seen:
                self.marker_seen = True
                matched_call = re.search(rb"call=(\d+)", line)
                self.marker_call = int(matched_call.group(1)) if matched_call else None
                self.marker_completed = self.schedule("consumer-parent", self.process.pid)
            if self.options.marker.startswith("owned-mcp") and line.startswith(b"owned-mcp-r10 launch=1 stage=22 ") and self.marker_completed is not None:
                self.marker_completed.set()
            if self.options.marker.startswith("owned-plugin") and line.startswith(b"owned-plugin-r12 "):
                fields = dict(re.findall(rb"(call|stage)=(\d+)", line))
                if fields.get(b"stage") == b"5" and fields.get(b"call") == str(self.marker_call).encode("ascii") and self.marker_completed is not None:
                    self.marker_completed.set()
            if self.options.marker.startswith("owned-pane") and line.startswith(b"owned-pane-r12 stage=20 ") and self.marker_completed is not None:
                self.marker_completed.set()
            if not line.startswith(b"owned-repl-r12 "):
                return
            fields = {key.decode("ascii"): int(value) for key, value in re.findall(rb"(call|stage|pid)=(\d+)", line)}
            call, stage, pid = (fields.get(key) for key in ("call", "stage", "pid"))
            if call is None or stage is None or pid is None:
                return
            if stage == 0 and len(self.calls) < 32:
                self.calls[call] = self.schedule("repl-spawn-parent", self.process.pid)
            elif stage == 1 and call in self.calls:
                self.calls[call].set()
                self.calls[call] = self.schedule("repl-child", pid)
            elif stage == 5 and call in self.calls:
                self.calls[call].set()

    def copy_output(self, source, destination):
        for line in iter(source.readline, b""):
            with self.output_lock:
                destination.buffer.write(line)
                destination.buffer.flush()
            self.observe(line)

    def run(self, command):
        self.process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        pumps = [
            threading.Thread(target=self.copy_output, args=(self.process.stdout, sys.stdout), daemon=True),
            threading.Thread(target=self.copy_output, args=(self.process.stderr, sys.stderr), daemon=True),
        ]
        for pump in pumps:
            pump.start()
        try:
            code = self.process.wait()
        finally:
            with self.state_lock:
                self.finished.set()
                if self.marker_completed is not None:
                    self.marker_completed.set()
                for completed in self.calls.values():
                    completed.set()
            if self.process.poll() is None:
                self.process.terminate()
                self.process.wait()
        for pump in pumps:
            pump.join()
        for sampler in self.samplers:
            sampler.join()
        self.emit(f"NATIVE_OBSERVER marker_seen={self.marker_seen} stack_reports={self.stack_reports} consumer_exit={code}")
        return code


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stack-tool", required=True)
    parser.add_argument("--marker", default="owned-mcp-r10 launch=1 stage=30")
    parser.add_argument("--sample-after", type=float, default=15)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    options = parser.parse_args()
    command = options.command[1:] if options.command[:1] == ["--"] else options.command
    if not command:
        parser.error("An actual consumer command is required.")
    return NativeStartupObserver(options).run(command)


if __name__ == "__main__":
    sys.exit(main())
