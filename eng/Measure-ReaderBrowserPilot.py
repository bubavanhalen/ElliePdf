import csv
import ctypes as ct
from ctypes import wintypes as wt
import json
import math
from pathlib import Path
import statistics
import subprocess
import sys
import time
import urllib.request

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parents[1] / "artifacts" / "reader-baseline"
RFC = (ROOT / "corpus/rfc9110.pdf").as_uri()
OTHER = (ROOT / "corpus/apollo11-mission-report.pdf").as_uri()
BROWSERS = {
    "Chrome": r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    "Edge": r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
}
SAMPLES = 5
CONDITIONS = ("cold-process", "resident", "warm-reopen")
user = ct.WinDLL("user32", use_last_error=True)
gdi = ct.WinDLL("gdi32", use_last_error=True)
user.SetProcessDpiAwarenessContext(ct.c_void_p(-4))
user.GetWindowThreadProcessId.argtypes = (wt.HWND, ct.POINTER(wt.DWORD))
user.GetWindowRect.argtypes = (wt.HWND, ct.POINTER(wt.RECT))
user.IsWindowVisible.argtypes = (wt.HWND,)
user.GetWindowDC.argtypes = (wt.HWND,)
user.GetWindowDC.restype = wt.HDC
user.ReleaseDC.argtypes = (wt.HWND, wt.HDC)
user.PrintWindow.argtypes = (wt.HWND, wt.HDC, wt.UINT)
user.PostMessageW.argtypes = (wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
window_callback = ct.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user.EnumWindows.argtypes = (window_callback, wt.LPARAM)
gdi.CreateCompatibleDC.argtypes = (wt.HDC,)
gdi.CreateCompatibleDC.restype = wt.HDC
gdi.CreateCompatibleBitmap.argtypes = (wt.HDC, ct.c_int, ct.c_int)
gdi.CreateCompatibleBitmap.restype = wt.HBITMAP
gdi.SelectObject.argtypes = (wt.HDC, wt.HANDLE)
gdi.SelectObject.restype = wt.HANDLE
gdi.GetBitmapBits.argtypes = (wt.HBITMAP, ct.c_long, ct.c_void_p)
gdi.DeleteObject.argtypes = (wt.HANDLE,)
gdi.DeleteDC.argtypes = (wt.HDC,)


def windows(pid):
    result = []

    @window_callback
    def add_window(hwnd, _):
        actual_pid = wt.DWORD()
        user.GetWindowThreadProcessId(hwnd, ct.byref(actual_pid))
        if actual_pid.value == pid and user.IsWindowVisible(hwnd):
            rect = wt.RECT()
            user.GetWindowRect(hwnd, ct.byref(rect))
            width, height = rect.right - rect.left, rect.bottom - rect.top
            if 900 <= width <= 1150 and 640 <= height <= 850:
                result.append((hwnd, width, height))
        return True

    user.EnumWindows(add_window, 0)
    return result


def capture(hwnd, width, height):
    source = user.GetWindowDC(hwnd)
    memory = gdi.CreateCompatibleDC(source)
    bitmap = gdi.CreateCompatibleBitmap(source, width, height)
    old = gdi.SelectObject(memory, bitmap)
    try:
        if not user.PrintWindow(hwnd, memory, 2):
            return None
        size = width * height * 4
        pixels = ct.create_string_buffer(size)
        if gdi.GetBitmapBits(bitmap, size, pixels) != size:
            return None
        # Exclude tabs, toolbar, and thumbnails. Keep the title and body text.
        image = Image.frombuffer("RGB", (width, height), pixels.raw, "raw", "BGRX", 0, 1)
        return image.crop((390, 235, 670, 670)).convert("L")
    finally:
        gdi.SelectObject(memory, old)
        gdi.DeleteObject(bitmap)
        gdi.DeleteDC(memory)
        user.ReleaseDC(hwnd, source)


def get_tabs(profile):
    port = (profile / "DevToolsActivePort").read_text().splitlines()[0]
    with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=2) as response:
        return json.load(response), port


def browser_arguments(name, profile):
    return [BROWSERS[name], f"--user-data-dir={profile}", "--guest", "--no-first-run",
            "--no-default-browser-check", "--disable-background-mode", "--remote-debugging-port=0",
            "--window-position=1120,104", "--window-size=1086,753"]


def close_browser(process):
    targets = windows(process.pid)
    if targets:
        for hwnd, _, _ in targets:
            user.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE on harness-owned windows.
        try:
            process.wait(3)
            return
        except subprocess.TimeoutExpired:
            pass
    if process.poll() is None:
        process.terminate()
        process.wait(3)


def reference(name, args):
    process = subprocess.Popen([*args, RFC])
    try:
        time.sleep(2)
        targets = windows(process.pid)
        result = capture(*targets[0]) if len(targets) == 1 else None
        if result is None:
            raise RuntimeError(f"{name} did not present one window at the requested size")
        pixels = np.asarray(result, dtype=np.int16)
        if np.count_nonzero(pixels < 100) < 500 or np.count_nonzero(pixels > 240) < 10_000:
            raise RuntimeError(f"{name} reference has insufficient document contrast")
        result.save(ROOT / f"{name.lower()}-rfc-reference.png")
        return pixels
    finally:
        close_browser(process)


def observe(process, expected, start):
    previous = start
    while time.perf_counter() - start < 8:
        for target in windows(process.pid):
            frame = capture(*target)
            now = time.perf_counter()
            if frame is not None:
                pixels = np.asarray(frame, dtype=np.int16)
                mismatch = np.mean(np.abs(pixels - expected) > 25)
                if mismatch < 0.012:
                    return round(1000 * (now - start), 2), round(1000 * (now - previous), 2)
        previous = time.perf_counter()
        time.sleep(0.004)
    return None, None


def trial(args, profile, expected, condition):
    if condition == "cold-process":
        start = time.perf_counter()
        process = subprocess.Popen([*args, RFC])
        try:
            return observe(process, expected, start)
        finally:
            close_browser(process)

    process = subprocess.Popen([*args, OTHER])
    try:
        time.sleep(1.6)
        if condition == "warm-reopen":
            subprocess.run([*args, RFC], timeout=5, check=True)
            if observe(process, expected, time.perf_counter())[0] is None:
                raise RuntimeError("Warm-up PDF failed to render")
            tabs, port = get_tabs(profile)
            target, = [tab for tab in tabs if tab["url"] == RFC and tab["type"] == "page"]
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/close/{target['id']}", timeout=2):
                pass
            time.sleep(0.25)
        start = time.perf_counter()
        dispatch = subprocess.Popen([*args, RFC])
        try:
            return observe(process, expected, start)
        finally:
            dispatch.wait(5)
    finally:
        close_browser(process)


def main():
    conditions = tuple(sys.argv[1:]) or CONDITIONS
    if any(condition not in CONDITIONS for condition in conditions):
        raise SystemExit(f"Pass one or more of {', '.join(CONDITIONS)}.")
    if not all((ROOT / "corpus" / file).is_file() for file in ("rfc9110.pdf", "apollo11-mission-report.pdf")):
        raise SystemExit(f"Download the two documented public PDFs to {ROOT / 'corpus'}.")
    references = {}
    for name in BROWSERS:
        profile = ROOT / "pilot-profiles" / name
        args = browser_arguments(name, profile)
        references[name] = (args, profile, reference(name, args))
    path = ROOT / ("browser-warm-open.csv" if conditions == ("warm-reopen",) else "browser-open.csv")
    with path.open("w", newline="") as output:
        writer = csv.writer(output)
        writer.writerow(("browser", "condition", "trial", "readable_ms", "sampling_interval_ms"))
        for condition in conditions:
            for repeat in range(1, SAMPLES + 1):
                browsers = list(BROWSERS if repeat % 2 else reversed(BROWSERS))
                for name in browsers:
                    milliseconds, sampling = trial(*references[name], condition)
                    row = (name, condition, repeat, milliseconds, sampling)
                    writer.writerow(row)
                    output.flush()
                    print(row, flush=True)
    with path.open(newline="") as source:
        rows = list(csv.DictReader(source))
    for condition in conditions:
        for name in BROWSERS:
            attempts = [row for row in rows if row["browser"] == name and row["condition"] == condition]
            values = sorted(float(row["readable_ms"]) for row in attempts if row["readable_ms"])
            if len(values) == len(attempts):
                print(name, condition, len(values), statistics.median(values), values[math.ceil(.95 * len(values)) - 1])
            else:
                print(name, condition, f"{len(attempts) - len(values)} of {len(attempts)} timed out")


if __name__ == "__main__":
    main()
