# Development

## Architecture

- `src/winairplay`: dashboard, settings, tray and independent connection states.
- `src/WinAirPlay.Core`: mDNS, SRP-6a, encrypted RTSP, binary plist, NTP, encrypted
  RTP, retransmission cache and timed sending.
- `native`: WASAPI capture on an MMCSS thread, preallocated PCM buffer and
  gradual drift correction using a 64-tap windowed sinc interpolator.

Each packet contains 352 frames, approximately 8 ms of audio. The sender uses
QPC and a high-resolution Windows timer. Startup waits for captured samples
rather than assuming a fixed sleep filled the buffer. Silent rendering keeps
the output clock active during pauses. The engine does not discard queued
samples or invent silence to conceal capture failures.

Settings: `%LOCALAPPDATA%/WinAirPlay/settings-v2.json`.
Logs: `%LOCALAPPDATA%/WinAirPlay/native-v2.log`, rotated at approximately 2 MB
with one previous file retained. Session entries identify the speaker name and
address. Metrics include send intervals, PCM reserve, applied drift, capture
discontinuities, missed playback deadlines and retransmission requests/misses.
Audio and pairing secrets are not recorded.

## Build and tests

Requires Windows x64, PowerShell and the .NET 8 SDK.

```powershell
./Build.ps1
```

The script downloads Zig 0.14.1 if needed, verifies its published SHA-256,
compiles the native engine, runs native buffer/spectral tests, builds the solution
and runs managed protocol tests. It publishes the self-contained executable,
bundling its native DLL and .NET runtime into one executable, plus a ZIP with the optional installer, into `dist`. `-SkipPublish` runs the build and tests without
publishing. Builds do not connect to speakers.

```powershell
# Protocol tests, without network or audio capture
dotnet run --project tests/WinAirPlay.Tests -c Release

# Ten seconds of WASAPI capture, without recording or transmission
dotnet run --project tests/WinAirPlay.Tests -c Release -- --capture

# Pairing and session setup, without audio
./dist/WinAirPlay.exe --probe SPEAKER_IP PORT

# Fifteen seconds of real PC audio, 100 ms target and 50% volume
./dist/WinAirPlay.exe --stream-test SPEAKER_IP PORT

# Optional live UI test: quit the app first; two distinct speakers on port 7000
dotnet run --project tests/WinAirPlay.UiTests -c Release -- SPEAKER_IP_1 SPEAKER_IP_2
```

The executable diagnostics write to `%LOCALAPPDATA%/WinAirPlay/probe-v2.log`.
The UI test streams real audio at the saved source, volume and latency settings.
It checks adding a second speaker, tray state, failure isolation, individual
disconnect, cancellation and disconnecting all while a connection is pending.
It restores local settings on normal completion and saves a dashboard image in
`build/ui-tests/`. Its executable needs incoming AirPlay UDP allowed through the
firewall, just like the app. A test pass does not verify audible quality or
inter-speaker synchronization.

Local migration backups and build tools remain under ignored `build/`; they
are not shipped. Generated executables, logs and compiler caches are excluded
from Git.

## Protocol references

- [HomeKit based pairings](https://openairplay.github.io/airplay-spec/pairing/hkp.html)
- [RTP streams](https://openairplay.github.io/airplay-spec/audio/rtp_streams.html)
- [AirPlay 2 sequence and clocks](https://github.com/music-assistant/airplay-cli/blob/main/DESIGN.md)

[GNU GPL v3.0](../LICENSE) (`GPL-3.0-only`).


## Release packaging

`./Build.ps1` produces three release assets:

- `dist/WinAirPlay.exe`: standalone Windows x64 app.
- `dist/WinAirPlay-win-x64.zip`: the app, license, README and optional Start menu installer.
- `dist/SHA256SUMS.txt`: SHA-256 checksums for both downloads.

The runtime extracts bundled native components to the normal .NET bundle cache on first launch. No DLL needs to sit beside the executable. Signing requires a publisher certificate; the build does not sign binaries.

Run `WinAirPlay.exe --check-runtime` from an otherwise empty directory and check its exit code. Zero confirms that bundled native audio code loads and audio outputs can be enumerated, without starting capture or connecting to a speaker.

For UI and volume regression checks without playback:

```powershell
dotnet run --project tests/WinAirPlay.UiTests -c Release -- --window
```

The UI test briefly opens windows and restores saved settings after normal completion. It does not establish audible receiver quality.
