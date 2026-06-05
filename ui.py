from __future__ import annotations

import ctypes
import os
import sys
import tkinter as tk
from typing import Callable, Optional

import customtkinter as ctk

import i18n

try:
    from PIL import Image
    _PIL = True
except ImportError:
    _PIL = False

ctk.set_appearance_mode("dark")

# ── Win11 dark palette ─────────────────────────────────────────────────────────
BG          = "#1c1c1c"   # window background
CARD        = "#2b2b2b"   # inactive device card
CARD_HV     = "#363636"   # hover
CARD_ACT    = "#21344a"   # active device card (accent tint)
BORDER      = "#323232"
BORDER_ACT  = "#35506f"
SEP         = "#2a2a2a"
ACCENT      = "#3b82f6"
ACCENT_HV   = "#2f6fd6"
TEXT        = "#ffffff"
SUB         = "#a8a8a8"
DIM         = "#6e6e6e"
GREEN       = "#3fb950"
TRACK       = "#3a3a3a"

FONT_TITLE = ("Segoe UI Semibold", 15)
FONT_SEC   = ("Segoe UI Semibold", 10)
FONT       = ("Segoe UI", 12)
FONT_SM    = ("Segoe UI", 11)

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
    monitor actually has the bar). Falls back to the primary monitor."""
    user32 = ctypes.windll.user32
    try:
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
    r = _RECT()
    try:
        user32.SystemParametersInfoW(0x30, 0, ctypes.byref(r), 0)
    except Exception:
        pass
    sw = user32.GetSystemMetrics(0) or r.right
    sh = user32.GetSystemMetrics(1) or r.bottom
    return ((0, 0, sw, sh), (r.left, r.top, r.right or sw, r.bottom or sh))


def _round_region(hwnd: int, radius: int = 16) -> None:
    """Clip a borderless window to a rounded rectangle. Uses the physical window
    size (GetWindowRect) so it stays correct under DPI scaling. Re-call after any
    resize — the region is pixel-based."""
    try:
        u = ctypes.windll.user32
        g = ctypes.windll.gdi32
        rc = _RECT()
        u.GetWindowRect(hwnd, ctypes.byref(rc))
        w, h = rc.right - rc.left, rc.bottom - rc.top
        if w <= 0 or h <= 0:
            return
        rgn = g.CreateRoundRectRgn(0, 0, w + 1, h + 1, radius, radius)
        u.SetWindowRgn(hwnd, rgn, True)
    except Exception:
        pass


# ── Device card ────────────────────────────────────────────────────────────────

def _lerp(c1: str, c2: str, t: float) -> str:
    """Interpolate two #rrggbb colors (t in 0..1)."""
    a = (int(c1[1:3], 16), int(c1[3:5], 16), int(c1[5:7], 16))
    b = (int(c2[1:3], 16), int(c2[3:5], 16), int(c2[5:7], 16))
    m = tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))
    return f"#{m[0]:02x}{m[1]:02x}{m[2]:02x}"


class _DeviceCard(ctk.CTkFrame):
    """One device row. The widgets are built ONCE; set_active() only recolors and
    shows/hides the volume row, so toggling connect/disconnect morphs smoothly
    instead of rebuilding (no flicker / "reload")."""

    def __init__(self, parent, name: str, device,
                 is_active: bool, volume: float,
                 on_click: Callable, on_volume: Callable):
        super().__init__(parent, fg_color=CARD, corner_radius=10, height=62)
        self.pack_propagate(False)   # fixed height → the popup never resizes on morph
        self._name      = name
        self._device    = device
        self._on_click  = on_click
        self._on_volume = on_volume
        self._is_active: Optional[bool] = None
        self._anim = None

        self._name_row = ctk.CTkFrame(self, fg_color="transparent")
        self._name_row.pack(fill="x", padx=12)
        self._dot = ctk.CTkLabel(self._name_row, text="●", font=("Segoe UI", 11),
                                 width=16)
        self._dot.pack(side="left")
        self._nm = ctk.CTkLabel(self._name_row, text=name, font=FONT, anchor="w")
        self._nm.pack(side="left", fill="x", expand=True)

        self._vol_row = ctk.CTkFrame(self, fg_color="transparent")
        self._pct = ctk.CTkLabel(self._vol_row, text="", text_color=SUB,
                                 font=FONT_SM, width=40, anchor="e")
        self._pct.pack(side="right")
        self._slider = ctk.CTkSlider(
            self._vol_row, from_=0, to=100, command=self._on_slide,
            progress_color=ACCENT, button_color="#ffffff",
            button_hover_color="#e6e6e6", fg_color=TRACK, height=16)
        self._slider.pack(side="left", fill="x", expand=True, padx=(0, 8))

        # Click the card/name to toggle; hover only highlights when idle.
        for wdg in (self, self._name_row, self._dot, self._nm):
            wdg.bind("<Button-1>", lambda e: self._on_click())
            wdg.bind("<Enter>", self._enter)
            wdg.bind("<Leave>", self._leave)

        self.set_active(is_active, volume)

    def _on_slide(self, v: float) -> None:
        self._pct.configure(text=f"{int(float(v))}%")
        self._on_volume(float(v))

    def _enter(self, e) -> None:
        if not self._is_active:
            self.configure(fg_color=CARD_HV)

    def _leave(self, e) -> None:
        if not self._is_active:
            self.configure(fg_color=CARD)

    def set_active(self, is_active: bool, volume: float) -> bool:
        """Returns True if the active state actually changed (height may differ).
        Structural change (slider show/hide) is instant; colours fade smoothly."""
        if is_active == self._is_active:
            return False
        first = self._is_active is None
        self._is_active = is_active
        if is_active:
            self.configure(border_width=1, border_color=BORDER_ACT, cursor="")
            # name anchored to the top, slider revealed below (fixed card height)
            self._name_row.pack_configure(expand=False, pady=(10, 2))
            self._slider.set(volume)
            self._pct.configure(text=f"{int(volume)}%")
            self._vol_row.pack(fill="x", padx=12, pady=(0, 8))
            colors = (CARD_ACT, GREEN, TEXT)
        else:
            self._vol_row.pack_forget()
            self.configure(border_width=0, cursor="hand2")
            # only the name → vertically centred in the fixed-height card
            self._name_row.pack_configure(expand=True, pady=0)
            colors = (CARD, DIM, SUB)
        if first:
            self.configure(fg_color=colors[0])
            self._dot.configure(text_color=colors[1])
            self._nm.configure(text_color=colors[2])
        else:
            self._animate_colors(*colors)
        return True

    def _animate_colors(self, card: str, dot: str, nm: str, n: int = 6) -> None:
        if self._anim is not None:
            try:
                self.after_cancel(self._anim)
            except Exception:
                pass
            self._anim = None
        _s = lambda c: c if isinstance(c, str) else c[-1]
        s_card = _s(self.cget("fg_color"))
        s_dot  = _s(self._dot.cget("text_color"))
        s_nm   = _s(self._nm.cget("text_color"))

        def _step(i: int) -> None:
            t = i / n
            self.configure(fg_color=_lerp(s_card, card, t))
            self._dot.configure(text_color=_lerp(s_dot, dot, t))
            self._nm.configure(text_color=_lerp(s_nm, nm, t))
            self._anim = self.after(16, lambda: _step(i + 1)) if i < n else None

        _step(0)


# ── Popup ──────────────────────────────────────────────────────────────────────

class PopupMenu(tk.Toplevel):
    def __init__(
        self,
        tk_root,
        *,
        get_devices:           Callable,
        get_active_devices:    Callable,
        get_device_volume:     Callable,
        on_connect:            Callable,
        on_volume_change:      Callable,
        get_input_devices:     Callable,
        get_active_input:      Callable,
        on_input_change:       Callable,
        on_quit:               Callable,
        get_startup_enabled:   Callable,
        on_startup_change:     Callable,
        get_startmenu_enabled: Callable,
        on_startmenu_change:   Callable,
        get_latency_ms:        Callable,
        on_latency_change:     Callable,
        on_language_change:    Callable,
    ):
        super().__init__(tk_root)
        self._tk              = tk_root
        self._get_devices     = get_devices
        self._get_active      = get_active_devices
        self._get_vol         = get_device_volume
        self._on_connect      = on_connect
        self._on_vol          = on_volume_change
        self._get_inputs      = get_input_devices
        self._get_input       = get_active_input
        self._on_input        = on_input_change
        self._on_quit         = on_quit
        self._get_startup     = get_startup_enabled
        self._on_startup      = on_startup_change
        self._get_startmenu   = get_startmenu_enabled
        self._on_startmenu    = on_startmenu_change
        self._get_latency     = get_latency_ms
        self._on_latency      = on_latency_change
        self._on_lang         = on_language_change

        self._visible         = False
        self._settings_open   = False
        self._logo            = None
        self._lat_after       = None   # debounce handle for the latency slider
        self._device_cards: dict[str, _DeviceCard] = {}

        self.withdraw()
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.configure(bg=BG)

        self._inner = ctk.CTkFrame(self, fg_color=BG, corner_radius=0,
                                   border_width=1, border_color=BORDER)
        self._inner.pack(fill="both", expand=True)

        self.bind("<FocusOut>", lambda e: self.after(150, self._check_hide))
        self.bind("<Escape>",   lambda e: self.hide())
        # Re-clip rounded corners AFTER each resize commits (settings open/close).
        self.bind("<Configure>", self._on_configure)

        self._load_logo()
        self._build_ui()

    # ── logo ─────────────────────────────────────────────────────────────────

    def _load_logo(self) -> None:
        if not _PIL:
            return
        try:
            base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
            img = Image.open(os.path.join(base, "WinAirPlayTransparent.png")).convert("RGBA")
            self._logo = ctk.CTkImage(light_image=img, dark_image=img, size=(24, 24))
        except Exception:
            self._logo = None

    # ── build structure once ───────────────────────────────────────────────────

    def _sep(self, parent) -> None:
        ctk.CTkFrame(parent, height=1, fg_color=SEP).pack(fill="x", pady=(6, 0))

    def _build_ui(self) -> None:
        body = self._inner

        # ── Header ──
        hdr = ctk.CTkFrame(body, fg_color="transparent")
        hdr.pack(fill="x", padx=PAD, pady=(12, 8))

        logo_row = ctk.CTkFrame(hdr, fg_color="transparent")
        logo_row.pack(side="left")
        if self._logo:
            ctk.CTkLabel(logo_row, image=self._logo, text="").pack(side="left", padx=(0, 8))
        ctk.CTkLabel(logo_row, text="WinAirPlay", text_color=TEXT,
                     font=FONT_TITLE).pack(side="left")

        self._gear = ctk.CTkButton(
            hdr, text="⚙", width=30, height=30, corner_radius=8,
            font=("Segoe UI", 16), fg_color="transparent", hover_color=CARD_HV,
            text_color=DIM, command=self._toggle_settings)
        self._gear.pack(side="right")

        ctk.CTkLabel(body, text=i18n.T("devices"), text_color=DIM, font=FONT_SEC,
                     anchor="w").pack(fill="x", padx=PAD + 4, pady=(6, 4))

        # ── Device section (rebuilt when the list changes) ──
        self._device_section = ctk.CTkFrame(body, fg_color="transparent")
        self._device_section.pack(fill="x", padx=PAD - 4)
        self._rebuild_devices()

        # ── Settings frame (persistent; packed/unpacked) ──
        self._settings_frame = ctk.CTkFrame(body, fg_color="transparent")
        self._build_settings()

        # ── Footer ──
        self._footer_sep = ctk.CTkFrame(body, height=1, fg_color=SEP)
        self._footer_sep.pack(fill="x", pady=(6, 0))
        footer = ctk.CTkFrame(body, fg_color="transparent")
        footer.pack(fill="x", padx=PAD - 4, pady=(4, 10))
        ctk.CTkButton(footer, text=i18n.T("quit"), width=80, height=30,
                      corner_radius=8, font=FONT_SM, fg_color="transparent",
                      hover_color=CARD_HV, text_color=SUB,
                      command=self._on_quit).pack(side="right")

    def _build_settings(self) -> None:
        sf = self._settings_frame

        self._sep(sf)
        ctk.CTkLabel(sf, text=i18n.T("audio_input"), text_color=DIM, font=FONT_SEC,
                     anchor="w").pack(fill="x", padx=PAD + 4, pady=(8, 4))
        self._input_section = ctk.CTkFrame(sf, fg_color="transparent")
        self._input_section.pack(fill="x", padx=PAD - 4)

        self._sep(sf)
        startup_row = ctk.CTkFrame(sf, fg_color="transparent")
        startup_row.pack(fill="x", padx=PAD + 4, pady=(8, 2))
        ctk.CTkLabel(startup_row, text=i18n.T("launch_startup"), text_color=SUB,
                     font=FONT_SM).pack(side="left")
        self._startup_switch = ctk.CTkSwitch(
            startup_row, text="", width=42, progress_color=ACCENT,
            button_color="#ffffff", command=lambda: self._on_startup(
                bool(self._startup_switch.get())))
        self._startup_switch.pack(side="right")
        if self._get_startup():
            self._startup_switch.select()

        startmenu_row = ctk.CTkFrame(sf, fg_color="transparent")
        startmenu_row.pack(fill="x", padx=PAD + 4, pady=(2, 2))
        ctk.CTkLabel(startmenu_row, text=i18n.T("start_menu"), text_color=SUB,
                     font=FONT_SM).pack(side="left")
        self._startmenu_switch = ctk.CTkSwitch(
            startmenu_row, text="", width=42, progress_color=ACCENT,
            button_color="#ffffff", command=lambda: self._on_startmenu(
                bool(self._startmenu_switch.get())))
        self._startmenu_switch.pack(side="right")
        if self._get_startmenu():
            self._startmenu_switch.select()

        # ── Latency slider ──
        self._sep(sf)
        ctk.CTkLabel(sf, text=i18n.T("latency"), text_color=DIM, font=FONT_SEC,
                     anchor="w").pack(fill="x", padx=PAD + 4, pady=(8, 4))
        lat_row = ctk.CTkFrame(sf, fg_color="transparent")
        lat_row.pack(fill="x", padx=PAD + 4, pady=(0, 2))
        self._lat_value = ctk.CTkLabel(lat_row, text="", text_color=SUB,
                                       font=FONT_SM, width=58, anchor="e")
        self._lat_value.pack(side="right")
        self._lat_slider = ctk.CTkSlider(
            lat_row, from_=20, to=500, number_of_steps=48, command=self._on_lat_slide,
            progress_color=ACCENT, button_color="#ffffff",
            button_hover_color="#e6e6e6", fg_color=TRACK, height=16)
        self._lat_slider.pack(side="left", fill="x", expand=True, padx=(0, 8))
        cur_ms = self._latency_ms()
        self._lat_slider.set(cur_ms)
        self._lat_value.configure(text=f"{cur_ms} ms")
        ctk.CTkLabel(sf, text=i18n.T("latency_hint"), text_color=DIM,
                     font=("Segoe UI", 10), anchor="w", justify="left",
                     wraplength=W - 2 * PAD).pack(fill="x", padx=PAD + 4, pady=(0, 4))

        self._sep(sf)
        ctk.CTkLabel(sf, text=i18n.T("language"), text_color=DIM, font=FONT_SEC,
                     anchor="w").pack(fill="x", padx=PAD + 4, pady=(8, 4))
        lang_box = ctk.CTkFrame(sf, fg_color="transparent")
        lang_box.pack(fill="x", padx=PAD - 4)
        cur = i18n.get_language()
        for code in i18n.LANGUAGES:
            def _pick(c=code):
                i18n.set_language(c)
                self._on_lang(c)
            self._radio_row(lang_box, code == cur, i18n.lang_label(code), _pick)

    def _radio_row(self, parent, selected: bool, label: str, command: Callable) -> None:
        mark = "●" if selected else "○"
        ctk.CTkButton(
            parent, text=f"   {mark}   {label}", anchor="w", font=FONT_SM,
            fg_color="transparent", hover_color=CARD_HV,
            text_color=(ACCENT if selected else SUB), corner_radius=8, height=30,
            command=command).pack(fill="x", pady=1)

    # ── latency slider ───────────────────────────────────────────────────────────

    def _latency_ms(self) -> int:
        try:
            return int(round(self._get_latency() / 10.0) * 10)
        except Exception:
            return 150

    def _on_lat_slide(self, v: float) -> None:
        ms = int(round(float(v) / 10.0) * 10)   # snap to 10ms steps
        self._lat_value.configure(text=f"{ms} ms")
        # Debounce: only commit (which reconnects active devices) once the user
        # settles, so dragging the slider doesn't restart the stream on every tick.
        if self._lat_after is not None:
            try: self.after_cancel(self._lat_after)
            except Exception: pass
        self._lat_after = self.after(600, lambda: self._commit_latency(ms))

    def _commit_latency(self, ms: int) -> None:
        self._lat_after = None
        self._on_latency(float(ms))

    # ── targeted updates ───────────────────────────────────────────────────────

    def _rebuild_devices(self) -> None:
        for w in self._device_section.winfo_children():
            w.destroy()
        self._device_cards.clear()

        devices = self._get_devices()
        active  = self._get_active()
        if devices:
            for name, device in devices.items():
                card = _DeviceCard(
                    self._device_section, name, device,
                    is_active=name in active,
                    volume=self._get_vol(name),
                    on_click=lambda d=device: self._on_connect(d),
                    on_volume=lambda v, n=name: self._on_vol(n, v),
                )
                card.pack(fill="x", pady=3)
                self._device_cards[name] = card
        else:
            ctk.CTkLabel(self._device_section, text=i18n.T("searching"),
                         text_color=DIM, font=FONT_SM, anchor="w").pack(
                fill="x", padx=8, pady=(2, 8))

    def _rebuild_inputs(self) -> None:
        for w in self._input_section.winfo_children():
            w.destroy()
        active_idx, _ = self._get_input()
        opts = [(None, i18n.T("sys_output"), active_idx is None)]
        for dev in self._get_inputs():
            opts.append((dev["index"], dev["name"], active_idx == dev["index"]))
        for idx, name, is_sel in opts:
            def _sel(i=idx, n=name):
                self._on_input(i, n)
            self._radio_row(self._input_section, is_sel, name, _sel)

    # ── public refresh ─────────────────────────────────────────────────────────

    def full_rebuild(self) -> None:
        """Full UI rebuild — only for language changes (rare)."""
        self._freeze()
        self._device_cards.clear()
        self._settings_open = False
        for w in self._inner.winfo_children():
            w.destroy()
        self._build_ui()
        if self._visible:
            self._place()
        self._thaw()

    def refresh(self) -> None:
        devices = self._get_devices()
        active  = self._get_active()
        if set(devices.keys()) != set(self._device_cards.keys()):
            self._freeze()
            self._rebuild_devices()
            self._thaw()
            if self._visible:
                self._place()
        else:
            # Cards are fixed-height, so morphing active state never changes the
            # window size — only the clicked card's colours/slider change.
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
        self.after(20, lambda: _round_region(self.winfo_id()))
        self.focus_force()
        self._visible = True

    def hide(self) -> None:
        self.withdraw()
        self._visible = False

    # ── settings toggle ────────────────────────────────────────────────────────

    def _toggle_settings(self) -> None:
        self._settings_open = not self._settings_open
        self._gear.configure(text_color=ACCENT if self._settings_open else DIM)
        if self._settings_open:
            self._rebuild_inputs()
            (self._startup_switch.select if self._get_startup()
             else self._startup_switch.deselect)()
            (self._startmenu_switch.select if self._get_startmenu()
             else self._startmenu_switch.deselect)()
            cur_ms = self._latency_ms()
            self._lat_slider.set(cur_ms)
            self._lat_value.configure(text=f"{cur_ms} ms")
            self._settings_frame.pack(fill="x", before=self._footer_sep)
        else:
            self._settings_frame.pack_forget()
        if self._visible:
            self._place()

    # ── WM_SETREDRAW (suppress OS redraws during rebuilds) ──────────────────────

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

    def _on_configure(self, e) -> None:
        if e.widget is self:
            _round_region(self.winfo_id())

    def _place(self) -> None:
        self.update_idletasks()
        (m_left, m_top, m_right, m_bottom), \
            (wa_left, wa_top, wa_right, wa_bottom) = _cursor_monitor()
        pw = W
        ph = self._inner.winfo_reqheight()

        top_anchored = wa_top > m_top
        if top_anchored:                # taskbar at top of this monitor
            x, y = wa_right - pw - 8, wa_top + 4
        elif wa_left > m_left:          # taskbar at left
            x, y = wa_left + 4, wa_bottom - ph - 8
        elif wa_right < m_right:        # taskbar at right
            x, y = wa_right - pw - 4, wa_bottom - ph - 8
        else:                           # taskbar at bottom (or none) → bottom-right
            x, y = wa_right - pw - 8, wa_bottom - ph - 4

        x = max(wa_left + 4, min(x, wa_right - pw - 4))
        y = max(wa_top + 4, min(y, wa_bottom - ph - 4))
        self.geometry(f"{pw}x{ph}+{x}+{y}")
        # Re-clip happens in _on_configure once the resize commits.

    def _check_hide(self) -> None:
        try:
            if not self.focus_displayof():
                self.hide()
        except Exception:
            self.hide()
