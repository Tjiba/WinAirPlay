"""Minimal i18n — EN/FR strings + language persistence (via config.py)."""
from __future__ import annotations

from winairplay import config

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


def load() -> None:
    global _current
    lang = config.get_setting("language", "en")
    if lang in _STRINGS:
        _current = lang


def save() -> None:
    config.set_setting("language", _current)


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
