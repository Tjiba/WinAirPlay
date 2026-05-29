"""Borderless dark popup for WinAirPlay — Windows 11 Fluent Design."""
from __future__ import annotations

import ctypes
import os
import tkinter as tk
from typing import Callable, Optional

import i18n

try:
    from PIL import Image, ImageTk
    _PIL = True
except ImportError:
    _PIL = False

# ── Win11 dark palette ─────────────────────────────────────────────────────────
BG          = "#202020"
SURFACE     = "#2C2C2C"
SURF_HV     = "#363636"
SURF_ACT    = "#1B3858"
WIN_BORDER  = "#181818"
CARD_BORDER = "#3A3A3A"
SEP         = "#282828"
TEXT        = "#FFFFFF"
TEXT_S      = "#9B9B9B"
DIM         = "#636363"
ACCENT      = "#0078D4"
GREEN       = "#4DB870"
GRAY_DOT    = "#454545"
TRACK_E     = "#3D3D3D"
TRACK_F     = ACCENT

FONT_TITLE  = ("Segoe UI Variable", 12, "bold")
FONT_BOLD   = ("Segoe UI Variable", 10, "bold")
FONT        = ("Segoe UI Variable", 10)
FONT_SM     = ("Segoe UI Variable", 9)
FONT_SEC    = ("Segoe UI Variable", 8, "bold")

W   = 360
PAD = 14

_WM_SETREDRAW    = 0x000B
_RDW_INVALIDATE  = 0x0001
_RDW_ERASE       = 0x0004
_RDW_ALLCHILDREN = 0x0040
_RDW_UPDATENOW   = 0x0100


# ── Win32 helpers ──────────────────────────────────────────────────────────────

class _RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class _POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class _MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_ulong), ("rcMonitor", _RECT),
                ("rcWork", _RECT), ("dwFlags", ctypes.c_ulong)]


def _cursor_monitor() -> tuple[tuple, tuple]:
    """Return (monitor_rect, work_rect) for the monitor under the cursor.

    Multi-monitor aware: a tray click leaves the cursor on the right monitor, so
    we anchor the popup there. rcWork is per-monitor (taskbar-aware on whichever
    monitor actually has the bar). Falls back to the primary monitor.
    """
    user32 = ctypes.windll.user32
    try:
        # HMONITOR is a HANDLE — restype MUST be c_void_p or it truncates on Win64.
        user32.MonitorFromPoint.restype = ctypes.c_void_p
        user32.MonitorFromPoint.argtypes = [_POINT, ctypes.c_ulong]
        user32.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(_MONITORINFO)]
        user32.GetCursorPos.argtypes = [ctypes.POINTER(_POINT)]

        pt = _POINT()
        user32.GetCursorPos(ctypes.byref(pt))
        hmon = user32.MonitorFromPoint(pt, 2)  # MONITOR_DEFAULTTONEAREST
        mi = _MONITORINFO()
        mi.cbSize = ctypes.sizeof(_MONITORINFO)
        if user32.GetMonitorInfoW(hmon, ctypes.byref(mi)):
            m, w = mi.rcMonitor, mi.rcWork
            return ((m.left, m.top, m.right, m.bottom),
                    (w.left, w.top, w.right, w.bottom))
    except Exception:
        pass
    # Fallback: primary monitor via SPI_GETWORKAREA + screen metrics
    r = _RECT()
    try:
        user32.SystemParametersInfoW(0x30, 0, ctypes.byref(r), 0)
    except Exception:
        pass
    sw = user32.GetSystemMetrics(0) or r.right
    sh = user32.GetSystemMetrics(1) or r.bottom
    return ((0, 0, sw, sh), (r.left, r.top, r.right or sw, r.bottom or sh))


def _dwm_style(hwnd: int) -> None:
    """Rounded corners + drop shadow."""
    try:
        v = ctypes.c_int(2)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 33, ctypes.byref(v), ctypes.sizeof(v))
        class _M(ctypes.Structure):
            _fields_ = [("l", ctypes.c_int), ("r", ctypes.c_int),
                        ("t", ctypes.c_int), ("b", ctypes.c_int)]
        ctypes.windll.dwmapi.DwmExtendFrameIntoClientArea(hwnd, ctypes.byref(_M(1, 1, 1, 1)))
    except Exception:
        pass


def _acrylic(hwnd: int) -> None:
    """Frosted-glass acrylic backdrop."""
    try:
        class _AP(ctypes.Structure):
            _fields_ = [("AccentState", ctypes.c_int), ("AccentFlags", ctypes.c_int),
                        ("GradientColor", ctypes.c_int), ("AnimationId", ctypes.c_int)]
        class _WCA(ctypes.Structure):
            _fields_ = [("Attribute", ctypes.c_int), ("pData", ctypes.c_void_p),
                        ("cbData", ctypes.c_size_t)]
        ap = _AP()
        ap.AccentState   = 4
        ap.GradientColor = ctypes.c_int(0xD8202020).value
        wca = _WCA()
        wca.Attribute = 19
        wca.cbData    = ctypes.sizeof(ap)
        wca.pData     = ctypes.cast(ctypes.byref(ap), ctypes.c_void_p)
        ctypes.windll.user32.SetWindowCompositionAttribute(hwnd, ctypes.byref(wca))
    except Exception:
        pass


def _repaint(widget: tk.Widget, color: str) -> None:
    try:
        widget.configure(bg=color)
    except tk.TclError:
        pass
    for child in widget.winfo_children():
        _repaint(child, color)


# ── Custom volume slider ────────────────────────────────────────────────────────

class VolumeSlider(tk.Canvas):
    _PAD = 8
    _TH  = 2
    _TR  = 6

    def __init__(self, parent, value: float = 50.0, on_change=None,
                 active: bool = True, bg: str = SURFACE, **kw):
        super().__init__(parent, height=22, highlightthickness=0, bd=0, bg=bg, **kw)
        self._val    = float(max(0, min(100, value)))
        self._cb     = on_change
        self._active = active
        self.bind("<Configure>", lambda e: self._draw())
        if active:
            self.bind("<Button-1>", self._seek)
            self.bind("<B1-Motion>", self._seek)

    def set(self, v: float) -> None:
        self._val = float(max(0, min(100, v)))
        self._draw()

    def _val_to_x(self, v: float) -> int:
        span = max(self.winfo_width() - self._PAD * 2, 1)
        return self._PAD + int(v / 100.0 * span)

    def _x_to_val(self, x: int) -> float:
        span = max(self.winfo_width() - self._PAD * 2, 1)
        return max(0.0, min(100.0, (x - self._PAD) / span * 100))

    def _pill(self, x1: int, y1: int, x2: int, y2: int, color: str) -> None:
        r = (y2 - y1) // 2
        x2 = max(x2, x1 + r * 2)
        self.create_rectangle(x1 + r, y1, x2 - r, y2, fill=color, outline="")
        self.create_oval(x1, y1, x1 + r * 2, y2, fill=color, outline="")
        self.create_oval(x2 - r * 2, y1, x2, y2, fill=color, outline="")

    def _draw(self) -> None:
        self.delete("all")
        w = self.winfo_width()
        if w < 4:
            return
        cy = self.winfo_height() // 2
        tx = self._val_to_x(self._val)
        self._pill(self._PAD, cy - self._TH, w - self._PAD, cy + self._TH, TRACK_E)
        fill = TRACK_F if self._active else DIM
        self._pill(self._PAD, cy - self._TH, max(self._PAD, tx), cy + self._TH, fill)
        if self._active:
            r = self._TR
            self.create_oval(tx - r, cy - r, tx + r, cy + r,
                             fill="white", outline="", width=0)

    def _seek(self, e: tk.Event) -> None:
        self._val = self._x_to_val(e.x)
        self._draw()
        if self._cb:
            self._cb(self._val)


# ── Toggle switch ─────────────────────────────────────────────────────────────

class ToggleSwitch(tk.Canvas):
    _W, _H = 38, 22

    def __init__(self, parent, value: bool = False, on_change=None,
                 bg: str = SURFACE, **kw):
        super().__init__(parent, width=self._W, height=self._H,
                         highlightthickness=0, bd=0, bg=bg, **kw)
        self._on = value
        self._cb = on_change
        self.bind("<Button-1>", self._click)
        self._draw()

    def set(self, v: bool) -> None:
        self._on = v
        self._draw()

    def _draw(self) -> None:
        self.delete("all")
        W, H  = self._W, self._H
        r     = H // 2
        track = ACCENT if self._on else TRACK_E
        self.create_oval(0, 0, H, H, fill=track, outline="")
        self.create_oval(W - H, 0, W, H, fill=track, outline="")
        self.create_rectangle(r, 0, W - r, H, fill=track, outline="")
        pad = 3
        tx  = W - r if self._on else r
        self.create_oval(tx - r + pad, pad, tx + r - pad, H - pad,
                         fill="white", outline="")

    def _click(self, e) -> None:
        self._on = not self._on
        self._draw()
        if self._cb:
            self._cb(self._on)


# ── Device card ────────────────────────────────────────────────────────────────

class DeviceCard(tk.Frame):
    def __init__(self, parent, name: str, device,
                 is_active: bool, volume: float,
                 on_click: Callable, on_volume: Callable):
        super().__init__(parent, bg=CARD_BORDER, padx=1, pady=1)
        self._name      = name
        self._on_click  = on_click
        self._on_volume = on_volume
        self._is_active = is_active
        self._build(is_active, volume)

    def set_active(self, is_active: bool, volume: float) -> None:
        if is_active == self._is_active:
            return
        self._is_active = is_active
        for w in self.winfo_children():
            w.destroy()
        self._build(is_active, volume)

    def _build(self, is_active: bool, volume: float) -> None:
        card_bg = SURF_ACT if is_active else SURFACE
        inner   = tk.Frame(self, bg=card_bg)
        inner.pack(fill="both", expand=True)

        if is_active:
            tk.Frame(inner, bg=ACCENT, width=3).pack(side="left", fill="y")

        body = tk.Frame(inner, bg=card_bg)
        body.pack(side="left", fill="both", expand=True,
                  padx=PAD, pady=(10, 6 if is_active else 10))

        name_row = tk.Frame(body, bg=card_bg, cursor="hand2")
        name_row.pack(fill="x")

        tk.Label(name_row, text="●", bg=card_bg,
                 fg=GREEN if is_active else GRAY_DOT,
                 font=("Segoe UI", 8)).pack(side="left", padx=(0, 8))
        tk.Label(name_row, text=self._name, bg=card_bg,
                 fg=TEXT if is_active else TEXT_S,
                 font=FONT_BOLD, anchor="w").pack(side="left", fill="x", expand=True)

        if is_active:
            vol_row = tk.Frame(body, bg=card_bg)
            vol_row.pack(fill="x", pady=(8, 0))

            pct = tk.Label(vol_row, text=f"{int(volume)}%", bg=card_bg,
                           fg=TEXT_S, font=FONT_SM, width=4, anchor="e")
            pct.pack(side="right")

            def _vol(v: float, _p=pct) -> None:
                _p.configure(text=f"{int(v)}%")
                self._on_volume(v)

            self._slider = VolumeSlider(vol_row, value=volume, on_change=_vol,
                                        active=True, bg=card_bg)
            self._slider.pack(side="left", fill="x", expand=True, padx=(0, 8))

        slider = getattr(self, "_slider", None)

        def _enter(e):
            if not is_active:
                _repaint(inner, SURF_HV)
                if slider:
                    slider.configure(bg=SURF_HV)

        def _leave(e):
            if not is_active:
                _repaint(inner, SURFACE)
                if slider:
                    slider.configure(bg=SURFACE)

        for w in self._walk(inner):
            if isinstance(w, VolumeSlider):
                continue
            try:
                w.bind("<Button-1>", lambda e: self._on_click())
                w.bind("<Enter>", _enter)
                w.bind("<Leave>", _leave)
            except tk.TclError:
                pass

    @staticmethod
    def _walk(widget: tk.Widget):
        yield widget
        for child in widget.winfo_children():
            yield from DeviceCard._walk(child)


# ── Popup ──────────────────────────────────────────────────────────────────────

class PopupMenu(tk.Toplevel):
    def __init__(
        self,
        tk_root: tk.Tk,
        *,
        get_devices:         Callable,
        get_active_devices:  Callable,
        get_device_volume:   Callable,
        on_connect:          Callable,
        on_volume_change:    Callable,
        get_input_devices:   Callable,
        get_active_input:    Callable,
        on_input_change:     Callable,
        on_quit:             Callable,
        get_startup_enabled: Callable,
        on_startup_change:   Callable,
        on_language_change:  Callable,
    ):
        super().__init__(tk_root)
        self._tk             = tk_root
        self._get_devices    = get_devices
        self._get_active     = get_active_devices
        self._get_vol        = get_device_volume
        self._on_connect     = on_connect
        self._on_vol         = on_volume_change
        self._get_inputs     = get_input_devices
        self._get_input      = get_active_input
        self._on_input       = on_input_change
        self._on_quit        = on_quit
        self._get_startup    = get_startup_enabled
        self._on_startup     = on_startup_change
        self._on_lang        = on_language_change
        self._visible        = False
        self._styled         = False
        self._settings_open  = False
        self._logo_img: Optional[object] = None
        self._device_cards: dict[str, DeviceCard] = {}

        self.withdraw()
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.configure(bg=WIN_BORDER)

        self._inner = tk.Frame(self, bg=BG)
        self._inner.pack(fill="both", expand=True, padx=1, pady=1)

        self._body = tk.Frame(self._inner, bg=BG)
        self._body.pack(fill="both", expand=True)

        self.bind("<FocusOut>", lambda e: self.after(150, self._check_hide))
        self.bind("<Escape>",   lambda e: self.hide())

        self._load_logo()
        self._build_ui()

    # ── logo ───────────────────────────────────────────────────────────────────

    def _load_logo(self) -> None:
        if not _PIL:
            return
        try:
            path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "WinAirPlayTransparent.png")
            img = Image.open(path).convert("RGBA").resize((26, 26), Image.LANCZOS)
            self._logo_img = ImageTk.PhotoImage(img)
        except Exception:
            self._logo_img = None

    # ── build structure once ───────────────────────────────────────────────────

    def _build_ui(self) -> None:
        # ── Header ──
        hdr = tk.Frame(self._body, bg=BG)
        hdr.pack(fill="x", padx=PAD, pady=(14, 12))

        logo_row = tk.Frame(hdr, bg=BG)
        logo_row.pack(side="left")
        if self._logo_img:
            tk.Label(logo_row, image=self._logo_img, bg=BG).pack(side="left", padx=(0, 8))
        tk.Label(logo_row, text="WinAirPlay", bg=BG, fg=TEXT, font=FONT_TITLE).pack(side="left")

        self._gear = tk.Label(hdr, text="⚙", bg=BG, fg=DIM,
                              font=("Segoe UI", 14), cursor="hand2")
        self._gear.pack(side="right")
        self._gear.bind("<Button-1>", lambda e: self._toggle_settings())
        self._gear.bind("<Enter>",    lambda e: self._gear.configure(fg=TEXT))
        self._gear.bind("<Leave>",    lambda e: self._gear.configure(
            fg=ACCENT if self._settings_open else DIM))

        tk.Frame(self._body, bg=SEP, height=1).pack(fill="x")

        tk.Label(self._body, text=i18n.T("devices"), bg=BG, fg=DIM,
                 font=FONT_SEC, anchor="w").pack(fill="x", padx=PAD, pady=(10, 6))

        # ── Device section (children rebuilt when list changes) ──
        self._device_section = tk.Frame(self._body, bg=BG)
        self._device_section.pack(fill="x")
        self._rebuild_devices()

        # ── Settings frame (persistent — shown/hidden with pack) ──
        self._settings_frame = tk.Frame(self._body, bg=BG)
        self._build_settings()
        # Not packed yet; inserted before footer sep when opened

        # ── Footer ──
        self._footer_sep = tk.Frame(self._body, bg=SEP, height=1)
        self._footer_sep.pack(fill="x", pady=(4, 0))

        footer = tk.Frame(self._body, bg=BG)
        footer.pack(fill="x", padx=PAD, pady=(6, 12))
        ql = tk.Label(footer, text=i18n.T("quit"), bg=BG, fg=DIM,
                      font=FONT_SM, cursor="hand2")
        ql.pack(side="right")
        ql.bind("<Button-1>", lambda e: self._on_quit())
        ql.bind("<Enter>",    lambda e: ql.configure(fg=TEXT_S))
        ql.bind("<Leave>",    lambda e: ql.configure(fg=DIM))

    def _build_settings(self) -> None:
        """Build settings content once inside self._settings_frame."""
        sf = self._settings_frame

        tk.Frame(sf, bg=SEP, height=1).pack(fill="x", pady=(4, 0))
        tk.Label(sf, text=i18n.T("audio_input"), bg=BG, fg=DIM,
                 font=FONT_SEC, anchor="w").pack(fill="x", padx=PAD, pady=(10, 6))

        self._input_section = tk.Frame(sf, bg=BG)
        self._input_section.pack(fill="x")

        tk.Frame(sf, bg=SEP, height=1).pack(fill="x", pady=(6, 0))
        startup_row = tk.Frame(sf, bg=BG)
        startup_row.pack(fill="x", padx=PAD, pady=(10, 4))
        tk.Label(startup_row, text=i18n.T("launch_startup"), bg=BG, fg=TEXT_S,
                 font=FONT_SM).pack(side="left")
        self._startup_switch = ToggleSwitch(startup_row, value=self._get_startup(),
                                            on_change=self._on_startup, bg=BG)
        self._startup_switch.pack(side="right")

        tk.Frame(sf, bg=SEP, height=1).pack(fill="x", pady=(6, 0))
        tk.Label(sf, text=i18n.T("language"), bg=BG, fg=DIM,
                 font=FONT_SEC, anchor="w").pack(fill="x", padx=PAD, pady=(10, 6))
        cur_lang = i18n.get_language()
        for code in i18n.LANGUAGES:
            is_sel = (code == cur_lang)
            row = tk.Frame(sf, bg=BG, cursor="hand2")
            row.pack(fill="x", padx=PAD, pady=(0, 2))
            tk.Label(row, text="●" if is_sel else "○", bg=BG,
                     fg=ACCENT if is_sel else DIM,
                     font=("Segoe UI", 8)).pack(side="left", padx=(0, 8))
            tk.Label(row, text=i18n.lang_label(code), bg=BG,
                     fg=TEXT if is_sel else TEXT_S,
                     font=FONT_SM, anchor="w").pack(side="left", fill="x", expand=True)

            def _pick(c=code):
                i18n.set_language(c)
                self._on_lang(c)
            for w in self._walk(row):
                w.bind("<Button-1>", lambda e, f=_pick: f())
                w.bind("<Enter>",    lambda e, r=row: _repaint(r, SURF_HV))
                w.bind("<Leave>",    lambda e, r=row: _repaint(r, BG))

    # ── targeted updates ───────────────────────────────────────────────────────

    def _rebuild_devices(self) -> None:
        """Rebuild device cards — called only when the device list changes."""
        for w in self._device_section.winfo_children():
            w.destroy()
        self._device_cards.clear()

        devices = self._get_devices()
        active  = self._get_active()

        if devices:
            for name, device in devices.items():
                card = DeviceCard(
                    self._device_section, name, device,
                    is_active = name in active,
                    volume    = self._get_vol(name),
                    on_click  = lambda d=device: self._on_connect(d),
                    on_volume = lambda v, n=name: self._on_vol(n, v),
                )
                card.pack(fill="x", padx=PAD, pady=(0, 4))
                self._device_cards[name] = card
        else:
            tk.Label(self._device_section, text=i18n.T("searching"),
                     bg=BG, fg=DIM, font=FONT_SM, anchor="w").pack(
                fill="x", padx=PAD, pady=(0, 8))

    def _rebuild_inputs(self) -> None:
        """Rebuild input option rows — called when selection changes."""
        for w in self._input_section.winfo_children():
            w.destroy()

        active_idx, _ = self._get_input()
        opts = [(None, i18n.T("sys_output"), active_idx is None)]
        for dev in self._get_inputs():
            opts.append((dev["index"], dev["name"], active_idx == dev["index"]))

        for idx, name, is_sel in opts:
            row = tk.Frame(self._input_section, bg=BG, cursor="hand2")
            row.pack(fill="x", padx=PAD, pady=(0, 2))
            tk.Label(row, text="●" if is_sel else "○", bg=BG,
                     fg=ACCENT if is_sel else DIM,
                     font=("Segoe UI", 8)).pack(side="left", padx=(0, 8))
            tk.Label(row, text=name, bg=BG,
                     fg=TEXT if is_sel else TEXT_S,
                     font=FONT_SM, anchor="w").pack(side="left", fill="x", expand=True)

            def _sel(i=idx, n=name): self._on_input(i, n)
            for w in self._walk(row):
                w.bind("<Button-1>", lambda e, f=_sel: f())
                w.bind("<Enter>",    lambda e, r=row: _repaint(r, SURF_HV))
                w.bind("<Leave>",    lambda e, r=row: _repaint(r, BG))

    # ── public refresh (incremental) ───────────────────────────────────────────

    def full_rebuild(self) -> None:
        """Full UI rebuild — only for language changes (rare)."""
        self._freeze()
        self._device_cards.clear()
        self._settings_open = False
        for w in self._body.winfo_children():
            w.destroy()
        self._build_ui()
        if self._visible:
            self._place()
        self._thaw()

    def refresh(self) -> None:
        devices = self._get_devices()
        active  = self._get_active()

        if set(devices.keys()) != set(self._device_cards.keys()):
            # Device list changed — freeze render, rebuild section, resize
            self._freeze()
            self._rebuild_devices()
            self._thaw()
            if self._visible:
                self._place()
        else:
            # Only active state changed — update cards in-place (no rebuild)
            for name, card in self._device_cards.items():
                card.set_active(name in active, self._get_vol(name))

        if self._settings_open:
            self._rebuild_inputs()

    # ── show / hide ────────────────────────────────────────────────────────────

    def toggle(self) -> None:
        self.hide() if self._visible else self.show()

    def show(self) -> None:
        self.refresh()
        self._place()
        self.deiconify()
        self.lift()
        if not self._styled:
            def _apply():
                hwnd = self.winfo_id()
                _dwm_style(hwnd)
                _acrylic(hwnd)
                self._styled = True
            self.after(30, _apply)
        self.focus_force()
        self._visible = True

    def hide(self) -> None:
        self.withdraw()
        self._visible = False

    # ── settings toggle ────────────────────────────────────────────────────────

    def _toggle_settings(self) -> None:
        self._settings_open = not self._settings_open
        self._gear.configure(fg=ACCENT if self._settings_open else DIM)
        if self._settings_open:
            self._rebuild_inputs()
            self._startup_switch.set(self._get_startup())
            self._settings_frame.pack(fill="x", before=self._footer_sep)
        else:
            self._settings_frame.pack_forget()
        if self._visible:
            self._place()

    # ── WM_SETREDRAW (suppress OS redraws during device list rebuild) ──────────

    def _freeze(self) -> None:
        try:
            ctypes.windll.user32.SendMessageW(self.winfo_id(), _WM_SETREDRAW, 0, 0)
        except Exception:
            pass

    def _thaw(self) -> None:
        try:
            hwnd = self.winfo_id()
            ctypes.windll.user32.SendMessageW(hwnd, _WM_SETREDRAW, 1, 0)
            ctypes.windll.user32.RedrawWindow(
                hwnd, None, None,
                _RDW_INVALIDATE | _RDW_ERASE | _RDW_ALLCHILDREN | _RDW_UPDATENOW,
            )
        except Exception:
            pass

    # ── geometry ───────────────────────────────────────────────────────────────

    def _place(self) -> None:
        self.update_idletasks()
        (m_left, m_top, m_right, m_bottom), \
            (wa_left, wa_top, wa_right, wa_bottom) = _cursor_monitor()
        pw = W + 2
        ph = self._inner.winfo_reqheight() + 2

        # Detect the taskbar side by comparing the work area to THIS monitor's
        # bounds (absolute virtual-desktop coords), so it works on any monitor.
        if wa_top > m_top:              # taskbar at top of this monitor
            x, y = wa_right - pw - 8, wa_top + 4
        elif wa_bottom < m_bottom:      # taskbar at bottom
            x, y = wa_right - pw - 8, wa_bottom - ph - 4
        elif wa_left > m_left:          # taskbar at left
            x, y = wa_left + 4, wa_bottom - ph - 8
        elif wa_right < m_right:        # taskbar at right
            x, y = wa_right - pw - 4, wa_bottom - ph - 8
        else:                           # no taskbar on this monitor → bottom-right
            x, y = wa_right - pw - 8, wa_bottom - ph - 4

        # Clamp into this monitor's work area
        x = max(wa_left + 4, min(x, wa_right - pw - 4))
        y = max(wa_top + 4, min(y, wa_bottom - ph - 4))
        self.geometry(f"{pw}x{ph}+{x}+{y}")

    def _check_hide(self) -> None:
        try:
            if not self.focus_displayof():
                self.hide()
        except Exception:
            self.hide()

    @staticmethod
    def _walk(widget: tk.Widget):
        yield widget
        for child in widget.winfo_children():
            yield from PopupMenu._walk(child)
