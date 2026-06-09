import atexit
import ctypes
import os
import logging
import subprocess
import sys
import threading
import time
from logging.handlers import RotatingFileHandler
import tkinter as tk
import winreg
from typing import Optional, Dict, Set

import pystray
from PIL import Image

import i18n
from capture import AudioCapture, list_loopback_devices
from raop import RAOPClient, set_raop_latency_ms, get_raop_latency_ms
from discovery import DeviceDiscovery, AirPlayDevice
from ui import PopupMenu

_LOG_DIR = os.path.join(os.environ.get("APPDATA", "."), "WinAirPlay")
os.makedirs(_LOG_DIR, exist_ok=True)
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s %(levelname)s %(message)s',
    handlers=[RotatingFileHandler(
        os.path.join(_LOG_DIR, 'winairplay.log'),
        maxBytes=2_000_000, backupCount=2, encoding='utf-8',
    )],
)
# pyatv's RAOP internals log per audio packet at DEBUG (millions of
# "Too slow to keep up" lines). Synchronous file writes on the asyncio loop
# thread add jitter to the real-time audio path and fill the disk — keep them
# at WARNING.
logging.getLogger('pyatv').setLevel(logging.WARNING)

RECONNECT_INTERVAL    = 2
RECONNECT_BACKOFF_MAX = 60     # cap (s) for a device that keeps failing to connect
HEALTHY_STREAM_SECONDS = 30    # a stream alive this long resets the backoff
BUFFER_DURATION    = 2.0
CHUNK_FRAMES       = 1024
BYTES_PER_FRAME    = 4   # 2ch × 2 bytes
DEFAULT_VOLUME     = 50.0

_SINGLE_INSTANCE_MUTEX = None  # keep the handle alive for the whole process
_SHOW_EXISTING_EVENT   = "WinAirPlay_ShowExisting_Event"  # 2nd launch → surface 1st


def acquire_single_instance() -> bool:
    """True if we're the only instance. A named Win32 mutex is released by the OS
    when the process dies (even on kill/crash), so a dead instance never blocks
    the next launch — while a live one prevents two apps fighting over WASAPI."""
    global _SINGLE_INSTANCE_MUTEX
    try:
        _SINGLE_INSTANCE_MUTEX = ctypes.windll.kernel32.CreateMutexW(
            None, False, "WinAirPlay_SingleInstance_Mutex"
        )
        return ctypes.windll.kernel32.GetLastError() != 183  # ERROR_ALREADY_EXISTS
    except Exception:
        return True  # never block startup on a mutex failure


def signal_existing_instance() -> None:
    """A second launch pings the already-running instance to surface its popup, then
    exits immediately. Replaces the old modal messagebox whose BLOCKING call left the
    rejected launch parked as a lingering 'ghost' WinAirPlay.exe in Task Manager."""
    try:
        k = ctypes.windll.kernel32
        k.OpenEventW.restype  = ctypes.c_void_p          # avoid 64-bit handle truncation
        k.OpenEventW.argtypes = [ctypes.c_uint, ctypes.c_bool, ctypes.c_wchar_p]
        k.SetEvent.argtypes   = [ctypes.c_void_p]
        k.CloseHandle.argtypes = [ctypes.c_void_p]
        EVENT_MODIFY_STATE = 0x0002
        h = k.OpenEventW(EVENT_MODIFY_STATE, False, _SHOW_EXISTING_EVENT)
        if h:
            k.SetEvent(h)
            k.CloseHandle(h)
    except Exception:
        pass


_REG_KEY  = r"Software\Microsoft\Windows\CurrentVersion\Run"
_REG_NAME = "WinAirPlay"


def _startup_cmd() -> str:
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}"'
    pythonw = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
    script  = os.path.abspath(__file__)
    return f'"{pythonw}" "{script}"'


def is_startup_enabled() -> bool:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _REG_KEY) as k:
            winreg.QueryValueEx(k, _REG_NAME)
            return True
    except OSError:
        return False


def set_startup(enabled: bool) -> None:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _REG_KEY, 0,
                            winreg.KEY_SET_VALUE) as k:
            if enabled:
                winreg.SetValueEx(k, _REG_NAME, 0, winreg.REG_SZ, _startup_cmd())
            else:
                winreg.DeleteValue(k, _REG_NAME)
    except OSError as e:
        logging.warning("[Startup] registry error: %s", e)


# ── Start Menu shortcut (makes the app searchable via the Windows key) ──────────

def _startmenu_lnk() -> str:
    appdata = os.environ.get("APPDATA", os.path.expanduser("~"))
    return os.path.join(appdata, "Microsoft", "Windows", "Start Menu",
                        "Programs", "WinAirPlay.lnk")


def is_startmenu_enabled() -> bool:
    return os.path.exists(_startmenu_lnk())


def set_startmenu(enabled: bool) -> None:
    # PowerShell spawn is slow (~1s) — never run it on the UI thread or the popup
    # freezes. Do it in the background; the toggle has already flipped visually.
    threading.Thread(target=_apply_startmenu, args=(enabled,), daemon=True).start()


def _apply_startmenu(enabled: bool) -> None:
    """Create/remove a Start Menu shortcut pointing at the CURRENT exe, so typing
    'winairplay' in Windows search finds the right binary."""
    lnk = _startmenu_lnk()
    try:
        if not enabled:
            if os.path.exists(lnk):
                os.remove(lnk)
            return

        if getattr(sys, "frozen", False):
            target, args = sys.executable, ""
        else:
            target = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
            args   = f'"{os.path.abspath(__file__)}"'
        workdir = os.path.dirname(target)

        os.makedirs(os.path.dirname(lnk), exist_ok=True)
        q = lambda s: s.replace("'", "''")  # escape for PowerShell single-quoted strings
        ps = (
            "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s');"
            "$s.TargetPath='%s';$s.Arguments='%s';$s.IconLocation='%s,0';"
            "$s.WorkingDirectory='%s';$s.Save()"
        ) % (q(lnk), q(target), q(args), q(target), q(workdir))
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
            creationflags=0x08000000,  # CREATE_NO_WINDOW — no console flash
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False,
        )
    except Exception as e:
        logging.warning("[StartMenu] %s", e)


def _resource_path(name: str) -> str:
    """Absolute path to a bundled resource. Works from source AND from a
    PyInstaller --onefile exe (assets are extracted to sys._MEIPASS)."""
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, name)


def _enable_pro_audio_priority():
    """Register the CURRENT thread with MMCSS "Pro Audio" so a busy desktop
    (launching an app, GC, GIL contention) can't preempt the capture/send loop
    long enough to overflow the WASAPI loopback ring — which silently drops
    samples (read uses exception_on_overflow=False) and is heard as crackle
    exactly during an app's startup CPU spike. Best-effort: never fatal.
    Returns (avrt_dll, handle) to revert later, or (None, None) on failure.
    """
    try:
        avrt = ctypes.windll.avrt
        # restype MUST be c_void_p — a HANDLE truncates to 32 bits otherwise (the
        # same ctypes pitfall as MonitorFromPoint), making the revert call fail.
        avrt.AvSetMmThreadCharacteristicsW.restype = ctypes.c_void_p
        avrt.AvSetMmThreadCharacteristicsW.argtypes = [
            ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_uint)
        ]
        avrt.AvRevertMmThreadCharacteristics.restype = ctypes.c_bool
        avrt.AvRevertMmThreadCharacteristics.argtypes = [ctypes.c_void_p]
        task_index = ctypes.c_uint(0)
        handle = avrt.AvSetMmThreadCharacteristicsW("Pro Audio",
                                                    ctypes.byref(task_index))
        if handle:
            logging.info("[AudioLoop] MMCSS 'Pro Audio' priority enabled")
            return avrt, handle
        logging.warning("[AudioLoop] MMCSS enable failed (err=%d) — running at normal priority",
                        ctypes.get_last_error())
    except Exception as e:
        logging.warning("[AudioLoop] MMCSS unavailable (%s) — running at normal priority", e)
    return None, None


def _make_icon_image() -> Image.Image:
    try:
        img = Image.open(_resource_path("WinAirPlayIcon.png")).convert("RGBA")
        bbox = img.getbbox()
        if bbox:
            img = img.crop(bbox)
        return img
    except Exception as e:
        # A missing/unreadable icon must never crash the app — use a fallback.
        logging.warning("[Icon] Falling back to generated tray icon: %s", e)
        return Image.new("RGBA", (64, 64), (0, 120, 212, 255))


class WinAirPlay:
    def __init__(self):
        # Multi-device streaming state
        self._active_devices: Dict[str, AirPlayDevice] = {}  # name → device (should be streaming)
        self._raop_clients:   Dict[str, RAOPClient]    = {}  # name → live client
        self._client_volumes: Dict[str, float]          = {}  # name → last-set volume

        self._streaming = False   # True while capture loop is running
        self._lock         = threading.Lock()
        self._capture_lock = threading.Lock()  # serializes all capture start/stop
        self._stop_event = threading.Event()
        self._reconnect_needed = threading.Event()
        # Per-device reconnect backoff (defuses the reconnect storm)
        self._reconnect_attempt_at: Dict[str, float] = {}
        self._reconnect_fails:      Dict[str, int]   = {}
        self._audio_loop_done = threading.Event()
        self._audio_loop_done.set()  # not running initially

        self._input_device_index: Optional[int] = None
        self._input_device_name:  str           = "Default"

        self._capture = AudioCapture(chunk_frames=CHUNK_FRAMES,
                                     device_index=self._input_device_index)

        self._devices:   Dict[str, AirPlayDevice] = {}
        self._discovery  = DeviceDiscovery(on_change=self._on_devices_changed)
        self._tray:      Optional[pystray.Icon] = None
        self._tk_root:   Optional[tk.Tk]        = None
        self._popup:     Optional[PopupMenu]    = None

    # ------------------------------------------------------------------ run

    def run(self) -> None:
        i18n.load()
        # Restore the saved RAOP latency (applies to every connection from now on)
        set_raop_latency_ms(i18n.get_setting("latency_ms", get_raop_latency_ms()))
        self._discovery.start()
        self._start_reconnect_thread()

        self._tk_root = tk.Tk()
        self._tk_root.withdraw()

        self._popup = PopupMenu(
            self._tk_root,
            get_devices          = lambda: self._devices,
            get_active_devices   = lambda: set(self._active_devices.keys()),
            get_device_volume    = self._get_device_volume,
            on_connect           = self._on_popup_connect,
            on_volume_change     = self._set_device_volume,
            get_input_devices    = self._list_input_devices,
            get_active_input     = lambda: (self._input_device_index, self._input_device_name),
            on_input_change      = self._on_input_select,
            on_quit              = self._quit,
            get_startup_enabled  = is_startup_enabled,
            on_startup_change    = set_startup,
            get_startmenu_enabled = is_startmenu_enabled,
            on_startmenu_change  = set_startmenu,
            get_latency_ms       = get_raop_latency_ms,
            on_latency_change    = self._on_latency_change,
            on_language_change   = self._on_language_change,
        )

        self._tray = pystray.Icon(
            "WinAirPlay", _make_icon_image(), "WinAirPlay",
            menu=pystray.Menu(
                pystray.MenuItem("Show", self._toggle_popup, default=True, visible=False),
            ),
        )
        self._tray.run_detached()
        # Surface our popup when a second launch pings us (clean single-instance).
        self._start_show_listener()
        # Best-effort RAOP teardown if we exit abnormally — avoid HomePod zombies.
        atexit.register(self._emergency_teardown)
        self._tk_root.mainloop()
        self._shutdown()

    def _start_show_listener(self) -> None:
        """First instance: create the named event a second launch signals, and bring
        our popup to the foreground when it fires. Daemon thread; dies with us."""
        try:
            k = ctypes.windll.kernel32
            k.CreateEventW.restype  = ctypes.c_void_p     # avoid 64-bit handle truncation
            k.CreateEventW.argtypes = [ctypes.c_void_p, ctypes.c_bool,
                                       ctypes.c_bool, ctypes.c_wchar_p]
            # auto-reset, initially non-signaled
            self._show_event = k.CreateEventW(None, False, False, _SHOW_EXISTING_EVENT)
        except Exception as e:
            logging.debug("[Startup] show-event unavailable: %s", e)
            self._show_event = None
        if not self._show_event:
            return
        threading.Thread(target=self._show_listener_loop, daemon=True,
                         name="show-listener").start()

    def _show_listener_loop(self) -> None:
        k = ctypes.windll.kernel32
        k.WaitForSingleObject.restype  = ctypes.c_uint
        k.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        while not self._stop_event.is_set():
            # 0.5s timeout so the thread notices shutdown; 0 == WAIT_OBJECT_0 (pinged).
            if k.WaitForSingleObject(self._show_event, 500) == 0:
                if self._tk_root and self._popup:
                    self._tk_root.after(0, self._popup.show)

    def _emergency_teardown(self) -> None:
        """atexit hook: if we exit via an uncaught exception (NOT a hard kill, which
        can't be intercepted), tear down RAOP sessions so the HomePod doesn't keep a
        dead session holding its single audio slot. No-op on the normal Quit path,
        which already cleared _raop_clients."""
        try:
            with self._lock:
                clients = list(self._raop_clients.values())
                self._raop_clients.clear()
            for c in clients:
                try:
                    c.disconnect()
                except Exception:
                    pass
        except Exception:
            pass

    # ------------------------------------------------------------------ popup

    def _toggle_popup(self, icon=None, item=None) -> None:
        if self._tk_root:
            self._tk_root.after(0, self._popup.toggle)

    def _refresh_menu(self) -> None:
        if self._tk_root and self._popup and self._popup._visible:
            self._tk_root.after(0, self._popup.refresh)

    def _on_language_change(self, lang: str) -> None:
        if self._tk_root and self._popup:
            self._tk_root.after(0, self._popup.full_rebuild)

    # ------------------------------------------------------------------ connect / disconnect

    def _on_popup_connect(self, device: AirPlayDevice) -> None:
        threading.Thread(target=self._toggle_device, args=(device,), daemon=True).start()

    def _toggle_device(self, device: AirPlayDevice) -> None:
        name = device.name
        with self._lock:
            if name in self._active_devices:
                # Disconnect this device
                client = self._raop_clients.pop(name, None)
                del self._active_devices[name]
                self._reconnect_attempt_at.pop(name, None)
                self._reconnect_fails.pop(name, None)
                no_more = not self._raop_clients
                if no_more:
                    self._streaming = False
            else:
                # Connect this device
                client  = None
                no_more = False
                vol     = self._client_volumes.get(name, DEFAULT_VOLUME)
                c       = RAOPClient()
                self._raop_clients[name]   = c
                self._active_devices[name] = device
                threading.Thread(
                    target=self._connect_and_stream, args=(device, c, vol), daemon=True
                ).start()

        # Refresh UI immediately — _active_devices already reflects new state
        self._refresh_menu()

        if client is not None:
            client.disconnect()
        if no_more:
            with self._lock:
                if not self._raop_clients:
                    self._capture.stop()

    def _connect_and_stream(self, device: AirPlayDevice, client: RAOPClient, volume: float) -> None:
        """Run in thread: connect, wait for ready, ensure audio loop is running."""
        client.connect(device.host, device.port, volume=volume,
                       et=device.et, md=device.md)

        # Start capture and audio loop early — feeder accumulates audio during
        # pyatv connection; _flush_stale() clears stale chunks when pyatv starts
        # reading, so audio flows immediately once pyatv is ready (not after).
        with self._lock:
            if not self._streaming and self._raop_clients:
                self._streaming = True
                self._audio_loop_done.clear()
                with self._capture_lock:
                    self._capture.start()
                threading.Thread(target=self._audio_loop, daemon=True).start()

        if not client._ready.wait(timeout=20):
            logging.error("[Connect] Timeout: %s", device.name)
            self._evict_client(device.name, client)
            return

        if not client._alive:
            logging.error("[Connect] Failed: %s", device.name)
            self._evict_client(device.name, client)
            return

        self._refresh_menu()

    def _evict_client(self, name: str, client: RAOPClient) -> None:
        with self._lock:
            if self._raop_clients.get(name) is client:
                self._raop_clients.pop(name, None)
                self._active_devices.pop(name, None)
        # A connect that timed out may still have a live asyncio loop trying to
        # finish the handshake — disconnect it so it can't become a zombie sender.
        threading.Thread(target=client.disconnect, daemon=True).start()
        self._refresh_menu()

    # ------------------------------------------------------------------ audio loop

    def _audio_loop(self) -> None:
        logging.info("[AudioLoop] Started")
        avrt, mmcss_handle = _enable_pro_audio_priority()
        try:
            while self._streaming and not self._stop_event.is_set():
                try:
                    pcm = self._capture.read_chunk()
                except OSError as e:
                    if not self._streaming or self._stop_event.is_set():
                        break
                    logging.warning("[AudioLoop] Capture error (%s) — restarting stream", e)
                    try:
                        with self._capture_lock:
                            self._capture.stop()
                            if not self._streaming or self._stop_event.is_set():
                                break
                            self._capture.start()
                        logging.info("[AudioLoop] Capture stream restarted")
                    except Exception as e2:
                        logging.error("[AudioLoop] Failed to restart capture: %s", e2)
                        break
                    continue

                with self._lock:
                    snapshot = list(self._raop_clients.items())

                dead = []
                for name, raop in snapshot:
                    if raop._alive and raop._proc is not None:
                        raop.send_chunk(pcm)
                    elif not raop._alive:
                        dead.append(name)

                if dead:
                    evicted = []
                    with self._lock:
                        for name in dead:
                            c = self._raop_clients.get(name)
                            if c is not None:
                                logging.warning("[AudioLoop] %s stream ended — will reconnect", name)
                                self._raop_clients.pop(name, None)
                                evicted.append(c)
                                # Keep in _active_devices so reconnect loop picks it up
                    # Tear each dead client down so its asyncio loop + pyatv
                    # connection can't linger as a zombie sender to the device.
                    for c in evicted:
                        threading.Thread(target=c.disconnect, daemon=True).start()
                    self._refresh_menu()
                    self._reconnect_needed.set()

                with self._lock:
                    if not self._raop_clients:
                        logging.info("[AudioLoop] DIAG exit: no clients left (streaming=%s, stop=%s)",
                                     self._streaming, self._stop_event.is_set())
                        self._streaming = False
                        break

            if not self._streaming:
                logging.info("[AudioLoop] DIAG exit: _streaming cleared externally")
            if self._stop_event.is_set():
                logging.info("[AudioLoop] DIAG exit: stop_event set")
        except Exception:
            logging.exception("[AudioLoop] Error")
        finally:
            self._streaming = False
            # Evict any clients still marked alive — the audio loop died under them
            # (e.g. capture OSError). Disconnect them so the reconnect loop can
            # re-establish cleanly; otherwise _alive=True would suppress reconnect.
            with self._lock:
                stranded = {n: c for n, c in self._raop_clients.items() if c._alive}
                for n in stranded:
                    self._raop_clients.pop(n, None)
                if not self._raop_clients:
                    with self._capture_lock:
                        self._capture.stop()
            for c in stranded.values():
                threading.Thread(target=c.disconnect, daemon=True).start()
            if stranded and not self._stop_event.is_set():
                self._reconnect_needed.set()
            self._audio_loop_done.set()
            if avrt and mmcss_handle:
                try: avrt.AvRevertMmThreadCharacteristics(mmcss_handle)
                except Exception: pass
            logging.info("[AudioLoop] Stopped")

    # ------------------------------------------------------------------ volume / input

    def _set_device_volume(self, device_name: str, value: float) -> None:
        vol = max(0.0, min(100.0, value))
        self._client_volumes[device_name] = vol
        with self._lock:
            client = self._raop_clients.get(device_name)
        if client:
            client.set_volume(vol)

    def _get_device_volume(self, device_name: str) -> float:
        return self._client_volumes.get(device_name, DEFAULT_VOLUME)

    def _list_input_devices(self) -> list:
        return self._capture.list_loopback_devices()

    def _on_input_select(self, idx: Optional[int], name: str) -> None:
        self._input_device_index = idx
        self._input_device_name  = name
        self._refresh_menu()
        threading.Thread(target=self._do_input_select, args=(idx, name), daemon=True).start()

    def _do_input_select(self, idx: Optional[int], name: str) -> None:
        logging.info("[Trigger] DIAG _do_input_select idx=%s name=%s", idx, name)
        with self._lock:
            was_streaming = self._streaming
            to_restart    = dict(self._active_devices)
            clients_old   = list(self._raop_clients.values())
            self._raop_clients.clear()
            self._streaming = False

        for c in clients_old:
            try: c.disconnect()
            except Exception: pass
        if was_streaming:
            self._audio_loop_done.wait(timeout=3.0)
            with self._capture_lock:
                self._capture.stop()

        with self._lock:
            self._capture._device_index = self._input_device_index

        for dname, device in to_restart.items():
            vol    = self._client_volumes.get(dname, DEFAULT_VOLUME)
            client = RAOPClient()
            with self._lock:
                self._raop_clients[dname]   = client
                self._active_devices[dname] = device
            threading.Thread(
                target=self._connect_and_stream, args=(device, client, vol), daemon=True
            ).start()

        self._refresh_menu()

    # ------------------------------------------------------------------ latency

    def _on_latency_change(self, ms: float) -> None:
        """UI slider (debounced) → persist + apply. RAOP latency is set at stream
        setup, so we restart active streams to make it audible immediately."""
        set_raop_latency_ms(ms)
        i18n.set_setting("latency_ms", get_raop_latency_ms())
        with self._lock:
            has_active = bool(self._raop_clients)
        if has_active:
            threading.Thread(target=self._restart_active_streams, daemon=True).start()

    def _restart_active_streams(self) -> None:
        """Disconnect every active client and reconnect it (same device/volume).
        Used to apply a new latency. Mirrors _do_input_select minus the device swap."""
        logging.info("[Trigger] DIAG _restart_active_streams (latency=%.0fms)", get_raop_latency_ms())
        with self._lock:
            was_streaming = self._streaming
            to_restart    = dict(self._active_devices)
            clients_old   = list(self._raop_clients.values())
            self._raop_clients.clear()
            self._streaming = False

        for c in clients_old:
            try: c.disconnect()
            except Exception: pass
        if was_streaming:
            self._audio_loop_done.wait(timeout=3.0)
            with self._capture_lock:
                self._capture.stop()

        for dname, device in to_restart.items():
            vol    = self._client_volumes.get(dname, DEFAULT_VOLUME)
            client = RAOPClient()
            with self._lock:
                self._raop_clients[dname]   = client
                self._active_devices[dname] = device
            threading.Thread(
                target=self._connect_and_stream, args=(device, client, vol), daemon=True
            ).start()
        self._refresh_menu()

    # ------------------------------------------------------------------ callbacks

    def _on_devices_changed(self, devices: Dict[str, AirPlayDevice]) -> None:
        self._devices = devices
        disappeared   = []
        with self._lock:
            for name in list(self._active_devices):
                if name not in devices:
                    disappeared.append((name, self._raop_clients.pop(name, None)))
                    del self._active_devices[name]
                    self._reconnect_attempt_at.pop(name, None)
                    self._reconnect_fails.pop(name, None)

        if disappeared:
            logging.info("[Trigger] DIAG devices disappeared from mDNS: %s",
                         [n for n, _ in disappeared])
        for name, client in disappeared:
            if client:
                threading.Thread(target=client.disconnect, daemon=True).start()

        self._refresh_menu()

    # ------------------------------------------------------------------ reconnect

    def _start_reconnect_thread(self) -> None:
        threading.Thread(target=self._reconnect_loop, daemon=True).start()

    def _reconnect_loop(self) -> None:
        while not self._stop_event.is_set():
            self._reconnect_needed.wait(timeout=RECONNECT_INTERVAL)
            self._reconnect_needed.clear()
            if self._stop_event.is_set():
                break

            now = time.monotonic()
            with self._lock:
                to_reconnect = []
                for name, dev in self._active_devices.items():
                    client = self._raop_clients.get(name)
                    if client is not None and client._alive:
                        continue  # connected, or a connect is still in flight
                    # Backoff: a device that keeps failing fast must not be
                    # hammered every 5s (that was the reconnect storm). A stream
                    # that stayed up a healthy while resets the counter so a
                    # one-off drop still reconnects immediately.
                    last = self._reconnect_attempt_at.get(name, 0.0)
                    if last and now - last >= HEALTHY_STREAM_SECONDS:
                        self._reconnect_fails[name] = 0
                    fails   = self._reconnect_fails.get(name, 0)
                    backoff = min(RECONNECT_INTERVAL * (2 ** fails), RECONNECT_BACKOFF_MAX)
                    if last and now - last < backoff:
                        continue  # still backing off
                    to_reconnect.append((name, dev))
                stale = []
                for name, _ in to_reconnect:
                    c = self._raop_clients.pop(name, None)
                    if c is not None:
                        stale.append(c)
                    self._reconnect_attempt_at[name] = now
                    self._reconnect_fails[name] = self._reconnect_fails.get(name, 0) + 1
            # Fully disconnect any stale client before replacing it, so its event
            # loop never runs alongside the new connection.
            for c in stale:
                threading.Thread(target=c.disconnect, daemon=True).start()

            for name, device in to_reconnect:
                if self._stop_event.is_set():
                    break
                logging.info("[Reconnect] Reconnecting %s", name)
                vol    = self._client_volumes.get(name, DEFAULT_VOLUME)
                client = RAOPClient()
                with self._lock:
                    self._raop_clients[name] = client
                threading.Thread(
                    target=self._connect_and_stream, args=(device, client, vol), daemon=True
                ).start()

    # ------------------------------------------------------------------ quit / shutdown

    def _quit(self, icon=None, item=None) -> None:
        self._stop_event.set()
        self._reconnect_needed.set()  # unblock reconnect loop
        # Close the UI instantly on the Tk thread — NO blocking work here, or the
        # window freezes white and Windows shows "(not responding)".
        if self._popup:
            try: self._popup.hide()
            except Exception: pass
        # destroy() (not quit()) so the popup window is actually removed, not
        # left as a dead frame; it ends mainloop and cascades to the popup child.
        if self._tk_root:
            self._tk_root.after(0, self._tk_root.destroy)
        # Everything that can block (mDNS stop, pyatv disconnects up to 8s each,
        # WASAPI teardown) runs off the UI thread.
        threading.Thread(target=self._stop_all, daemon=True).start()

    def _stop_all(self) -> None:
        if self._tray:
            try: self._tray.stop()
            except Exception: pass
        try: self._discovery.stop()
        except Exception: pass
        with self._lock:
            clients = list(self._raop_clients.values())
            self._raop_clients.clear()
            self._streaming = False
        for c in clients:
            try: c.disconnect()
            except Exception: pass
        with self._capture_lock:
            try: self._capture.stop()
            except Exception: pass

    def _shutdown(self) -> None:
        with self._capture_lock:
            try: self._capture.terminate()
            except Exception: pass
        try: self._discovery.stop()
        except Exception: pass


def main():
    if not acquire_single_instance():
        # Don't pop a blocking messagebox (it left a lingering ghost process). Ping
        # the running instance to show itself, then exit immediately and cleanly.
        logging.info("[Startup] Already running — surfacing the existing instance and exiting.")
        signal_existing_instance()
        return
    app = WinAirPlay()
    app.run()


if __name__ == "__main__":
    main()
