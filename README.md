# WinAirPlay

Play Windows audio on your HomePods and AirPlay speakers.

A small app in your system tray, with a dark Studio interface and shared or individual volume controls.

**[Download WinAirPlay-Setup.exe](https://github.com/Tjiba/WinAirPlay/releases/latest/download/WinAirPlay-Setup.exe)** · [All releases](https://github.com/Tjiba/WinAirPlay/releases)

## Get started

1. Download **WinAirPlay-Setup.exe** and open it.
2. Follow the graphical installer, then launch WinAirPlay from the Start menu. No separate .NET installation or DLL is needed.
3. Connect your PC and speakers to the same local network.
4. Click **Stream** next to a speaker. Repeat to play on more than one.

Requires Windows 10 or 11, 64-bit Intel/AMD. Allow WinAirPlay through Windows Firewall on your private network if prompted.

The executable is not code-signed. Windows may display an unknown-publisher warning.

The installer supports French and English, installs for your Windows account without administrator access, and adds WinAirPlay to **Settings → Apps → Installed apps**. To update, run the new installer; it offers to close the running app and preserves saved settings. No ZIP extraction or commands are needed.

For portable use, download **WinAirPlay.exe** or the portable ZIP instead. These do not register an installation.

## Everyday controls

- **Tray icon:** left-click to open the menu in the bottom-right corner; right-click for more actions.
- **Stop:** disconnect one speaker. **Stop all:** disconnect every speaker and cancel pending reconnections.
- **Volume:** use the shared slider, or uncheck **Settings → Use the same volume for all speakers** for a slider per speaker. Individual levels are remembered. **Mute / Unmute** restores the previous level during the current app session.
- **Settings → Add speaker by IP…:** manually save a speaker's name, IP address and port if automatic discovery fails. Use its **…** menu to edit or remove it after stopping its connection and any pending retry.
- **Audio source:** select the Windows output directly from the homepage after stopping playback and pending retries.
- **Refresh:** rescan the network and audio outputs.
- **Errors:** remain on the speaker card; open **… → Details** for the technical message.
- **×:** follows **Keep playing when closing the window**. **Esc** always hides the menu. Right-click the tray icon and choose **Quit** to exit.

## Settings

Choose the Windows audio output to capture and the target latency. Stop all connections and pending retries before changing either. Audio outputs refresh automatically; **Follow the default Windows output** reconnects active sessions when the default output changes, with a brief playback interruption.

**Reconnect after network loss or sleep** is enabled by default. Failed connections retry after 2, 4, 8, 16 and then 30 seconds between attempts. **Cancel**, **Stop all** and **Quit** cancel retries. Pairing rejection and invalid protocol data require manual retry. Only connections requested during the current app session are restored; launching the app never starts playback automatically.

Choose **English** or **Français** under **Language**. The interface updates when settings are saved; technical protocol errors remain in their original language.

Enable **Launch at Windows startup** to launch at sign-in. Enable **Start minimized to the system tray** to keep the menu hidden when the app starts. Keep the executable in the same location after enabling startup, or reinstall and save the setting again.

## Troubleshooting

**No speakers appear:** check that the PC and speakers are on the same network, not an isolated guest network. Check the private-network firewall permission, use **Refresh**, or open **Settings → Add speaker by IP…** as a fallback.

**No sound:** check the selected **Audio source**, Windows playback, and speaker volume. Then stop and reconnect the speaker.

**Audio delay or interruptions:** try a higher target latency in Settings. The default 100 ms is a target, not a guarantee of total audible delay.

**Need logs:** right-click the tray icon → **Open log**. Settings and logs are stored in `%LOCALAPPDATA%\WinAirPlay`.

## Compatibility

HomePods and compatible AirPlay 2 speakers are supported. Legacy AirPlay 1, permanent PIN pairing and password entry are not supported.

HomePod and HomePod mini illustrations are selected automatically from the model advertised over the local network. No IP entry is needed. Speakers that do not advertise a recognized model keep a generic illustration.

Multiple speakers use independent connections. They can play together, but this is not a synchronized AirPlay group: audible offsets between rooms are possible.

## Remove WinAirPlay

Choose **Quit**, then open **Windows Settings → Apps → Installed apps → WinAirPlay → Uninstall**. Uninstallation removes the app, Start menu shortcut and startup entry, while preserving saved settings and logs. For a portable copy, turn off **Launch at Windows startup**, quit and delete the executable. Delete `%LOCALAPPDATA%\WinAirPlay` only if you also want to remove saved settings and logs.

## Development

Build instructions, diagnostics and protocol details: [Development guide](https://github.com/Tjiba/WinAirPlay/blob/main/docs/DEVELOPMENT.md).

Copyright (c) 2026 Timo. Licensed under the [GNU GPL v3.0](LICENSE) (`GPL-3.0-only`).
