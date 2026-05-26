<div align="center">
  <img src="WinAirPlayTransparent.png" width="80" />
  <h1>WinAirPlay</h1>
  <p>Stream Windows audio to Apple TV, HomePod or any AirPlay speaker.<br/>No subscription. No setup. One double-click.</p>

  <a href="../../releases/latest">
    <img src="https://img.shields.io/github/v/release/Tjiba/WinAirPlay?style=for-the-badge&label=Download&color=0078D4" />
  </a>
</div>

---

## How it works

1. Download `WinAirPlay.exe` from [Releases](../../releases/latest)
2. Double-click — a speaker icon appears in your system tray
3. Click the icon → pick your AirPlay device
4. Audio streams instantly

> **Windows SmartScreen:** click "More info" → "Run anyway" on first launch.

---

## Requirements

- Windows 10 or later
- AirPlay device on the same Wi-Fi network

---

## For Developers

```powershell
pip install -r requirements.txt
python main.py          # run from source
python -m pytest tests/ # run tests
python build.py         # build WinAirPlay.exe → dist\
```
