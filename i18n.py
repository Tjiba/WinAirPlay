"""Minimal i18n — EN/FR strings + language persistence."""
from __future__ import annotations
import json
import os

_STRINGS: dict[str, dict[str, str]] = {
    "en": {
        "devices":          "DEVICES",
        "searching":        "Searching for devices…",
        "audio_input":      "AUDIO INPUT",
        "sys_output":       "System output (default)",
        "launch_startup":   "Launch at startup",
        "start_menu":       "Add to Start Menu",
        "quit":             "Quit",
        "language":         "LANGUAGE",
        "latency":          "LATENCY",
        "latency_hint":     "Lower = tighter A/V sync; raise it if audio crackles on "
                            "weak Wi-Fi. Reconnects active devices to apply.",
    },
    "fr": {
        "devices":          "APPAREILS",
        "searching":        "Recherche d'appareils…",
        "audio_input":      "ENTRÉE AUDIO",
        "sys_output":       "Sortie système (défaut)",
        "launch_startup":   "Lancer au démarrage",
        "start_menu":       "Ajouter au menu Démarrer",
        "quit":             "Quitter",
        "language":         "LANGUE",
        "latency":          "LATENCE",
        "latency_hint":     "Plus bas = vidéo plus synchro ; monte-la si ça grésille "
                            "en Wi-Fi faible. Reconnecte les appareils actifs.",
    },
}

_LANG_LABELS = {"en": "English", "fr": "Français"}
LANGUAGES = list(_LANG_LABELS.keys())

_current = "en"


def _config_path() -> str:
    base = os.environ.get("APPDATA", os.path.expanduser("~"))
    folder = os.path.join(base, "WinAirPlay")
    os.makedirs(folder, exist_ok=True)
    return os.path.join(folder, "config.json")


def load() -> None:
    global _current
    try:
        with open(_config_path(), encoding="utf-8") as f:
            data = json.load(f)
        lang = data.get("language", "en")
        if lang in _STRINGS:
            _current = lang
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        pass


def save() -> None:
    try:
        path = _config_path()
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except (FileNotFoundError, json.JSONDecodeError):
            data = {}
        data["language"] = _current
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
    except OSError:
        pass


def get_setting(key: str, default=None):
    """Read an arbitrary persisted setting from config.json."""
    try:
        with open(_config_path(), encoding="utf-8") as f:
            return json.load(f).get(key, default)
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return default


def set_setting(key: str, value) -> None:
    """Persist an arbitrary setting into config.json (merge, never clobber)."""
    try:
        path = _config_path()
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except (FileNotFoundError, json.JSONDecodeError):
            data = {}
        data[key] = value
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
    except OSError:
        pass


def get_language() -> str:
    return _current


def set_language(lang: str) -> None:
    global _current
    if lang in _STRINGS:
        _current = lang
        save()


def T(key: str) -> str:
    return _STRINGS[_current].get(key, key)


def lang_label(code: str) -> str:
    return _LANG_LABELS.get(code, code)
