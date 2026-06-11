"""Persisted app settings — %APPDATA%/WinAirPlay/config.json.

Read-modify-write is serialized by a process-wide lock so concurrent writers
(latency slider thread, language change on the UI thread) can't clobber each
other's keys.
"""
from __future__ import annotations
import json
import os
import threading

_lock = threading.Lock()


def _config_path() -> str:
    base = os.environ.get("APPDATA", os.path.expanduser("~"))
    folder = os.path.join(base, "WinAirPlay")
    os.makedirs(folder, exist_ok=True)
    return os.path.join(folder, "config.json")


def _read_all() -> dict:
    try:
        with open(_config_path(), encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return {}


def get_setting(key: str, default=None):
    with _lock:
        return _read_all().get(key, default)


def set_setting(key: str, value) -> None:
    with _lock:
        data = _read_all()
        data[key] = value
        try:
            with open(_config_path(), "w", encoding="utf-8") as f:
                json.dump(data, f, indent=2)
        except OSError:
            pass
