"""Exercise the actual tray pause/resume methods with a private data root and published worker."""
import ctypes
import os
import shutil
import signal
import sqlite3
import subprocess
import tempfile
import time
import unittest
from contextlib import closing
from ctypes import wintypes
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
DRIVER = REPO / 'tests/Tracker.Tray.Tests/bin/Release/net10.0-windows/Tracker.Tray.Tests.exe'
TRAY = Path(os.getenv('TRACKER_TEST_TRAY', REPO / 'artifacts/phase2/windows-x64/CodexTracker.Tray.exe'))


class ProcessEntry(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('usage', wintypes.DWORD), ('pid', wintypes.DWORD),
                ('heap', ctypes.c_size_t), ('module', wintypes.DWORD), ('threads', wintypes.DWORD),
                ('parent', wintypes.DWORD), ('priority', wintypes.LONG), ('flags', wintypes.DWORD),
                ('exe', wintypes.WCHAR * 260)]


def children(parent, name):
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    api.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snap = api.CreateToolhelp32Snapshot(2, 0)
    if snap == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    matches = []
    try:
        entry = ProcessEntry()
        entry.size = ctypes.sizeof(entry)
        ok = api.Process32FirstW(wintypes.HANDLE(snap), ctypes.byref(entry))
        while ok:
            if entry.parent == parent and entry.exe == name:
                matches.append(entry.pid)
            ok = api.Process32NextW(wintypes.HANDLE(snap), ctypes.byref(entry))
    finally:
        api.CloseHandle(wintypes.HANDLE(snap))
    return matches


class TrayWorker(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        folder = Path(temp.name)
        self.root, self.db, self.settings = folder/'input', folder/'tracker.sqlite', folder/'preferences'
        sessions = self.root/'sessions'
        sessions.mkdir(parents=True)
        shutil.copyfile(REPO/'tests/fixtures/turn.jsonl', sessions/'rollout-fixture.jsonl')
        self.host = None
        self.addCleanup(self.stop)

    def start(self):
        self.host = subprocess.Popen([DRIVER, 'host', self.db, self.root, self.settings, TRAY],
                                     stdin=subprocess.PIPE, text=True, creationflags=subprocess.CREATE_NO_WINDOW)

    def stop(self):
        if self.host and self.host.poll() is None:
            self.command('exit')
            self.host.wait(8)
        if self.host:
            self.host.stdin.close()

    def command(self, cmd):
        self.host.stdin.write(cmd+'\n')
        self.host.stdin.flush()

    def worker(self):
        for supervisor in children(self.host.pid, TRAY.name):
            for pid in children(supervisor, 'CodexTracker.exe'):
                return pid

    def wait(self, predicate):
        deadline = time.monotonic()+15
        while time.monotonic() < deadline:
            result = predicate()
            if result:
                return result
            time.sleep(.1)
        self.fail("Timed out waiting for "+str(predicate))

    def total(self):
        if not self.db.is_file():
            return None
        try:
            with closing(sqlite3.connect(self.db.as_uri()+'?mode=ro', uri=True, timeout=.2)) as c:
                return c.execute('SELECT sum(total) FROM TokenLedger').fetchone()[0]
        except sqlite3.OperationalError:
            return None

    def test_pause_persistence_resume_exit(self):
        self.start()
        before = self.wait(self.total)
        self.command('pause')
        self.wait(lambda: (self.settings/'monitor.paused').exists() and not self.worker())
        self.stop()
        self.start()
        time.sleep(1.2)
        self.assertIsNone(self.worker())
        self.command('resume')
        self.wait(self.worker)
        self.assertEqual(before, self.total())
        self.assertFalse((self.settings/'monitor.paused').exists())

    def test_worker_failure_then_owner_crash(self):
        self.start()
        before = self.wait(self.total)
        first = self.wait(self.worker)
        os.kill(first, signal.SIGTERM)
        second = self.wait(lambda: (p if (p := self.worker()) != first else None))
        self.assertNotEqual(first, second)
        supervisor = children(self.host.pid, TRAY.name)[0]
        self.host.kill()
        self.host.wait(8)
        self.wait(lambda: second not in children(supervisor, 'CodexTracker.exe'))
        self.stop()
        self.start()
        self.wait(self.worker)
        self.assertEqual(self.total(), before)


if __name__ == '__main__':
    unittest.main(verbosity=2)
