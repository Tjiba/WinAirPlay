"""Build script: generates WinAirPlay.ico then runs PyInstaller."""
import os
import subprocess
import sys

ROOT     = os.path.dirname(os.path.abspath(__file__))
ICO      = os.path.join(ROOT, "WinAirPlay.ico")
PNG      = os.path.join(ROOT, "WinAirPlayTransparent.png")
PNG_ICON = os.path.join(ROOT, "WinAirPlayIcon.png")


def make_ico() -> None:
    from PIL import Image
    img = Image.open(PNG).convert("RGBA")
    img.save(ICO, format="ICO",
             sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"  icon  → {ICO}")


def build() -> None:
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--onefile",         # single .exe — users download and run directly
        "--noconsole",       # no terminal window
        "--name", "WinAirPlay",
        f"--icon={ICO}",
        f"--add-data={PNG};.",
        f"--add-data={PNG_ICON};.",
        "--collect-all=pyatv",          # pyatv has many dynamic imports
        "--collect-all=zeroconf",
        "--collect-all=cryptography",   # used by pyatv for pairing
        "--hidden-import=pyaudiowpatch",
        "--hidden-import=pystray._win32",
        "--hidden-import=PIL._tkinter_finder",
        "--hidden-import=zeroconf._utils.ipaddress",
        "--hidden-import=zeroconf._handlers.answers",
        "main.py",
    ]
    print("  running PyInstaller…")
    result = subprocess.run(cmd, cwd=ROOT)
    if result.returncode != 0:
        print("\nBuild FAILED — see PyInstaller output above.")
        sys.exit(result.returncode)


def main() -> None:
    print("[1/2] Generating icon…")
    make_ico()
    print("[2/2] Building executable…")
    build()
    exe = os.path.join(ROOT, "dist", "WinAirPlay.exe")
    print(f"\nBuild OK  →  {exe}")
    print("Upload dist\\WinAirPlay.exe to GitHub Releases.")


if __name__ == "__main__":
    main()
