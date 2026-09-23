# WinAirPlay

Play Windows audio on your HomePods and AirPlay speakers.

A small app in your system tray, with a compact speaker menu and shared or individual volume controls.

**[Download WinAirPlay.exe](https://github.com/Tjiba/WinAirPlay/releases/latest/download/WinAirPlay.exe)** · [All releases](https://github.com/Tjiba/WinAirPlay/releases)

## Get started

1. Download **WinAirPlay.exe** and keep it in a permanent folder.
2. Open it. No separate .NET installation or DLL is needed.
3. Connect your PC and speakers to the same local network.
4. Click **Listen** next to a speaker. Repeat to play on more than one.

Requires Windows 10 or 11, 64-bit Intel/AMD. Allow WinAirPlay through Windows Firewall on your private network if prompted.

The executable is not code-signed. Windows may display an unknown-publisher warning.

For a Start menu shortcut, download **WinAirPlay-win-x64.zip**, extract everything, then double-click **Install.cmd**. This installs for your Windows account without administrator access. Quit an existing copy before updating.

## Everyday controls

- **Tray icon:** left-click to open the menu in the bottom-right corner; right-click for more actions.
- **Stop:** disconnect one speaker. **Stop all:** disconnect every speaker.
- **Volume:** use the shared slider, or uncheck **Settings → Use the same volume for all HomePods** for a slider per speaker. Individual levels are remembered.
- **Add:** enter a speaker's IP address if it does not appear automatically.
- **× / Esc:** hide the menu while audio keeps playing. Right-click the tray icon and choose **Quit** to exit.

## Settings

Choose the Windows audio output to capture and the target latency. Disconnect all speakers before changing either.

Enable **Launch at Windows startup** to launch at sign-in. Enable **Start minimized to the system tray** to keep the menu hidden when the app starts. Keep the executable in the same location after enabling startup, or reinstall and save the setting again.

## Troubleshooting

**No speakers appear:** check that the PC and speakers are on the same network, not an isolated guest network. Check the private-network firewall permission, or try **Add** with the speaker's IP address.

**No sound:** check the selected **Audio source**, Windows playback, and speaker volume. Then stop and reconnect the speaker.

**Audio delay or interruptions:** try a higher target latency in Settings. The default 100 ms is a target, not a guarantee of total audible delay.

**Need logs:** right-click the tray icon → **Open log**. Settings and logs are stored in `%LOCALAPPDATA%\WinAirPlay`.

## Compatibility

HomePods and compatible AirPlay 2 speakers are supported. Legacy AirPlay 1, permanent PIN pairing and password entry are not supported.

Multiple speakers use independent connections. They can play together, but this is not a synchronized AirPlay group: audible offsets between rooms are possible.

## Remove WinAirPlay

Turn off **Launch at Windows startup**, choose **Quit**, then delete the portable executable or the installed folder at `%LOCALAPPDATA%\Programs\WinAirPlay`. If installed, remove the WinAirPlay shortcut from the Start menu. Delete `%LOCALAPPDATA%\WinAirPlay` only if you also want to remove saved settings and logs.

## Development

Build instructions, diagnostics and protocol details: [Development guide](https://github.com/Tjiba/WinAirPlay/blob/main/docs/DEVELOPMENT.md).

Copyright (c) 2026 Timo. Licensed under the [GNU GPL v3.0](LICENSE) (`GPL-3.0-only`).
