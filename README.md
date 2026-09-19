# WinAirPlay 2

Windows system audio streaming to AirPlay speakers. C# / .NET 8 / WinForms,
native C++ / WASAPI capture and a direct AirPlay implementation. No Python,
pyatv or external audio process is required.

## Usage

1. Extract `dist/WinAirPlay2-win-x64.zip`.
2. Keep `WinAirPlay.exe` and `winairplay_audio.dll` together.
3. Open the app. Select the Windows audio output in **Settings**.
4. Click **Listen** on each speaker you want to connect.

Multiple speakers can play simultaneously. **Disconnect** stops only that
speaker; **Disconnect all** in the tray menu stops every connection, including
pending ones. Failed or cancelled connections do not stop other speakers.
The volume slider controls all connected speakers, without changing PC volume.

A left click on the tray icon toggles the dark dashboard. A right click opens
the quick menu. Closing or minimizing the window keeps playback running by
default; use **Quit** to exit. Startup and close behavior are configurable in
**Settings**. Disconnect all speakers before changing the source or latency.

The PC and speakers must share a network. Allow incoming mDNS, clock and
retransmission traffic through Windows Firewall for the app executable.

## Compatibility and latency

Supported receivers accept AirPlay 2 transient pairing, NTP timing and stereo
16-bit / 44.1 kHz PCM. Permanent PIN pairing, password entry in the UI and legacy
AirPlay 1 receivers are not implemented.

Each speaker has an independent session and capture of the same Windows output.
Playback is simultaneous, but this is **not a synchronized AirPlay group**:
audible offsets between speakers remain possible.

**100 ms is the default target, not measured end-to-end latency.** Capture aims
to retain about 20 ms and advertises the remaining delay to the receiver. A
higher receiver-reported minimum is honored and logged. Speakers can add their
own delay; sender statistics do not establish acoustic or video synchronization.

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
native DLL and ZIP into `dist`. `-SkipPublish` runs the build and tests without
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

[MIT license](LICENSE).
