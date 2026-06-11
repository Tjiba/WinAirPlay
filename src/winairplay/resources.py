"""Locate bundled assets, from source checkout or frozen exe alike."""
import os
import sys


def resource_path(name: str) -> str:
    """Absolute path to a file in assets/. Works from a source checkout
    (<repo>/assets) AND from a PyInstaller --onefile exe (assets/ is extracted
    under sys._MEIPASS)."""
    if hasattr(sys, "_MEIPASS"):
        base = sys._MEIPASS
    else:
        # this file: <repo>/src/winairplay/resources.py → repo root is 3 up
        base = os.path.dirname(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))))
    return os.path.join(base, "assets", name)
