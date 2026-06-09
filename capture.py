import logging
import time
import pyaudiowpatch as pyaudio
import numpy as np
from dataclasses import dataclass
from typing import Optional


TARGET_SAMPLE_RATE = 44100
TARGET_CHANNELS = 2
TARGET_SAMPLE_WIDTH = 2  # bytes (int16)
CHUNK_FRAMES = 1024

# Only synthesize silence once the WASAPI render endpoint has been idle this long
# (i.e. the user genuinely paused). A momentary buffer dip during active playback
# refills within ~1 chunk (~21ms at 48kHz), so this threshold must sit comfortably
# above that — otherwise we'd inject silence mid-stream and cause crackle/drift.
IDLE_SILENCE_AFTER_S = 0.05
POLL_INTERVAL_S = 0.002


@dataclass
class AudioFormat:
    sample_rate: int
    channels: int
    sample_width: int

    @property
    def needs_resample(self) -> bool:
        return (
            self.sample_rate != TARGET_SAMPLE_RATE
            or self.channels != TARGET_CHANNELS
            or self.sample_width != TARGET_SAMPLE_WIDTH
        )


def list_loopback_devices() -> list:
    """Return all WASAPI loopback devices (one-shot enumeration for menus).
    NOTE: prefer AudioCapture.list_loopback_devices() when a capture instance
    exists — it reuses the live _pa to avoid concurrent PyAudio/WASAPI crashes."""
    pa = pyaudio.PyAudio()
    try:
        return [dict(d) for d in pa.get_loopback_device_info_generator()]
    except Exception as e:
        logging.error("[Capture] Failed to list loopback devices: %s", e)
        return []
    finally:
        pa.terminate()


class AudioCapture:
    """
    WASAPI loopback capture.  PyAudio is kept alive for the entire app
    lifetime (one instance per WinAirPlay app) to avoid the Windows WASAPI
    bug where devices temporarily disappear right after pa.terminate().
    Call terminate() only when the app is shutting down.
    """

    def __init__(self, chunk_frames: int = CHUNK_FRAMES, device_index: Optional[int] = None):
        self._chunk_frames = chunk_frames
        self._device_index = device_index
        self._pa = pyaudio.PyAudio()   # kept alive; NOT terminated between sessions
        self._stream = None
        self._format: Optional[AudioFormat] = None
        self._reset_resampler()
        # --- pause/resume silence ---
        self._in_silence = False
        self._silence_since = 0.0
        self._silence_deadline = 0.0   # absolute monotonic target for real-time silence pacing
        self._diag_reads = 0

    # ------------------------------------------------------------------ public

    def start(self) -> None:
        device = self._find_loopback()
        if device is None:
            raise RuntimeError(
                "No WASAPI loopback device found. "
                "Ensure an audio output device is active."
            )
        sr = int(device["defaultSampleRate"])
        ch = device["maxInputChannels"]
        logging.info("[Capture] Opening loopback: %s | %d Hz | %d ch",
                     device["name"], sr, ch)
        self._format = AudioFormat(sample_rate=sr, channels=ch, sample_width=2)
        self._reset_resampler()  # fresh continuity state per stream/device/format
        self._stream = self._pa.open(
            format=pyaudio.paInt16,
            channels=ch,
            rate=sr,
            input=True,
            input_device_index=device["index"],
            frames_per_buffer=self._chunk_frames,
        )
        logging.info("[Capture] Stream open (needs_resample=%s)", self._format.needs_resample)

    def stop(self) -> None:
        """Close the stream but keep PyAudio alive for the next session."""
        if self._stream:
            try:
                self._stream.stop_stream()
                self._stream.close()
            except Exception as e:
                logging.warning("[Capture] Error closing stream: %s", e)
            self._stream = None
        logging.info("[Capture] Stream closed")

    def list_loopback_devices(self) -> list:
        """Enumerate loopback devices using the live _pa instance (thread-safe)."""
        try:
            return [dict(d) for d in self._pa.get_loopback_device_info_generator()]
        except Exception as e:
            logging.error("[Capture] list: %s", e)
            return []

    def terminate(self) -> None:
        """Full teardown — call only on app exit."""
        self.stop()
        if self._pa:
            self._pa.terminate()
            self._pa = None

    def read_chunk(self) -> bytes:
        """Read one chunk of PCM, normalized to 44100 Hz / stereo / int16.

        WASAPI loopback only produces data at the device's real-time clock, so a
        full chunk becomes available exactly once per chunk-period. We read it as
        soon as it is ready — this paces the loop to real time and returns ONLY
        real audio (no phantom frames). We must NEVER inject silence the instant
        the buffer dips below a chunk (the old bug): the consumer drains the ring
        faster than real time, so it dips constantly during active playback, and
        injecting silence there interleaves gaps into the stream (crackle) while
        over-producing frames (steadily growing latency / desync).

        Only when the render endpoint is genuinely idle — the user paused all
        audio — do we synthesize real-time-paced silence, to keep the downstream
        RAOP sender paced and the AirPlay session warm. A blocking read() can't be
        used directly because it would stall forever during such a pause.
        """
        # Snapshot the stream: a concurrent stop() sets self._stream = None, and
        # racing that into self._stream.get_read_available() crashed read_chunk with
        # "'NoneType' object has no attribute 'get_read_available'" (logs), killing
        # the audio loop. Hold a local ref — if stop() closes THIS object mid-read
        # PyAudio raises OSError, which the audio loop already handles by restarting.
        stream = self._stream
        if stream is None:
            raise OSError("capture stream is closed")
        chunk_dur = self._chunk_frames / TARGET_SAMPLE_RATE
        deadline = time.monotonic() + IDLE_SILENCE_AFTER_S
        while True:
            avail = stream.get_read_available()
            if avail >= self._chunk_frames:
                resuming = self._in_silence
                # --- LIVE-STREAM RESYNC (fixes startup glitch AND the crackle when
                # switching video/tab) --- WASAPI loopback presents a backlog in two
                # cases: (a) startup/stall → several chunks queue up; (b) resuming from
                # a silence gap → the sub-chunk frames left unread when we entered
                # silence are now STALE (they're the audio from BEFORE the gap), and
                # replaying them right after the injected silence is exactly the blip/
                # crackle heard on tab switch. In both cases, for a LIVE stream we want
                # "now": drop everything older than the freshest chunk. We only do this
                # on a real backlog (≥2 chunks) or on resume — never during steady
                # playback (avail hovers ~1 chunk), so normal audio is untouched.
                if avail > self._chunk_frames and (avail >= 2 * self._chunk_frames or resuming):
                    stale = avail - self._chunk_frames
                    stream.read(stale, exception_on_overflow=False)  # drop stale, keep freshest
                    self._rs_primed = False  # re-prime resampler across the discontinuity
                    logging.info("[Capture] DIAG resync: dropped %d stale frames (avail=%d, resuming=%s)",
                                 stale, avail, resuming)
                raw = stream.read(self._chunk_frames, exception_on_overflow=False)
                if resuming:  # DIAG: real audio came back after a silence gap
                    logging.info("[Capture] DIAG audio RESUMED after %.1fs silent (avail=%d)",
                                 time.monotonic() - self._silence_since, avail)
                    self._in_silence = False
                if self._format and self._format.needs_resample:
                    return self._resample(raw)
                return raw
            # Produce silence if we're ALREADY mid-pause (immediately — no 50ms
            # re-poll) or the endpoint has been idle past the grace threshold. The
            # old code re-ran the full 50ms poll before EVERY silence chunk, so during
            # a sustained pause silence was produced ~3x slower than real time; pyatv
            # fell far behind its real-time anchor and, at a small (120ms) device
            # buffer, the HomePod underran and took ~1min to recover (no sound after a
            # short pause).
            if self._in_silence or time.monotonic() >= deadline:
                now = time.monotonic()
                if not self._in_silence:  # entered a silence gap (pause/idle)
                    logging.info("[Capture] DIAG entering SILENCE (no loopback data >%.0fms, avail=%d)",
                                 IDLE_SILENCE_AFTER_S * 1000, avail)
                    self._in_silence = True
                    self._silence_since = now
                    self._silence_deadline = now   # anchor real-time pacing to now
                # Absolute-deadline pacing: advance the target by one chunk and sleep
                # until it, so the long-term silence rate is EXACTLY real time.
                # time.sleep() alone runs ~0.6ms long per 23ms chunk on Windows →
                # ~0.5s of accumulated drift over a 17s pause, which at a 120ms buffer
                # is enough to underrun the HomePod. The absolute deadline self-corrects.
                self._silence_deadline += chunk_dur
                sleep_for = self._silence_deadline - time.monotonic()
                if sleep_for > 0:
                    time.sleep(sleep_for)
                return self._silence_chunk()
            time.sleep(POLL_INTERVAL_S)

    def _silence_chunk(self) -> bytes:
        return b'\x00' * (self._chunk_frames * TARGET_CHANNELS * TARGET_SAMPLE_WIDTH)

    # ------------------------------------------------------------------ private

    def _find_loopback(self) -> Optional[dict]:
        if self._device_index is not None:
            try:
                dev = self._pa.get_device_info_by_index(self._device_index)
                if dev.get("isLoopbackDevice", False):
                    return dict(dev)
            except OSError:
                pass
            logging.warning("[Capture] Device index %d invalid, falling back to default",
                            self._device_index)

        try:
            wasapi_info = self._pa.get_host_api_info_by_type(pyaudio.paWASAPI)
            default_name = self._pa.get_device_info_by_index(
                wasapi_info["defaultOutputDevice"]
            )["name"]
        except Exception as e:
            logging.error("[Capture] Cannot query WASAPI default output: %s", e)
            default_name = ""

        loopbacks = [dict(d) for d in self._pa.get_loopback_device_info_generator()]
        logging.debug("[Capture] Found %d loopback(s): %s",
                      len(loopbacks), [d["name"] for d in loopbacks])

        if not loopbacks:
            return None

        for dev in loopbacks:
            if default_name and (default_name in dev["name"] or dev["name"] in default_name):
                return dev

        logging.warning("[Capture] No loopback matched '%s', using first: '%s'",
                        default_name, loopbacks[0]["name"])
        return loopbacks[0]

    def _reset_resampler(self) -> None:
        """Reset the continuous-resampler carry state (call per stream/format)."""
        # Next output position, in input-sample units, relative to the start of
        # the next input block. Kept across chunks so resampling is phase-continuous
        # and the average rate is exact (no per-chunk boundary glitch / drift).
        self._rs_frac: float = 0.0
        self._rs_prev_l: float = 0.0   # last left/right input sample of prev block
        self._rs_prev_r: float = 0.0
        self._rs_primed: bool = False

    def _resample(self, data: bytes) -> bytes:
        """Normalize a native PCM chunk to 44100 Hz / stereo / int16."""
        fmt = self._format
        samples = np.frombuffer(data, dtype=np.int16).astype(np.float32)

        # --- downmix/upmix to stereo at the native rate ---
        if fmt.channels == 1:
            left = samples
            right = samples
        elif fmt.channels == 2:
            left = samples[0::2]
            right = samples[1::2]
        else:
            reshaped = samples.reshape(-1, fmt.channels)
            left = reshaped[:, 0].copy()
            right = reshaped[:, 1].copy()

        # --- continuous sample-rate conversion ---
        if fmt.sample_rate != TARGET_SAMPLE_RATE:
            left, right = self._resample_rate(left, right, fmt.sample_rate)

        out = np.empty(len(left) * 2, dtype=np.float32)
        out[0::2] = left
        out[1::2] = right
        return np.clip(out, -32768, 32767).astype(np.int16).tobytes()

    def _resample_rate(self, left: np.ndarray, right: np.ndarray, src_rate: int):
        """Stateful linear resampler, continuous across chunk boundaries.

        Each call carries the fractional read position and the previous block's
        last sample, so the interpolation grid never restarts at a chunk edge.
        That removes the periodic boundary discontinuity AND keeps the long-term
        rate exact (e.g. 48000→44100 yields 940/941 frames alternating, averaging
        the true 940.8) — both prior sources of audible artifacts and slow drift.
        """
        step = src_rate / TARGET_SAMPLE_RATE
        L = len(left)
        if L == 0:
            return left, right

        if not self._rs_primed:
            self._rs_prev_l = float(left[0])
            self._rs_prev_r = float(right[0])
            self._rs_frac = 0.0
            self._rs_primed = True

        start = self._rs_frac
        # No output position lands in this block — advance carry, stash tail, done.
        if start > L - 1:
            self._rs_frac = start - L
            self._rs_prev_l = float(left[-1])
            self._rs_prev_r = float(right[-1])
            return np.empty(0, np.float32), np.empty(0, np.float32)

        # Augment with the previous block's tail at index 0 (orig index -1) so a
        # position in [-1, 0) interpolates across the chunk boundary.
        aug_l = np.empty(L + 1, dtype=np.float32)
        aug_r = np.empty(L + 1, dtype=np.float32)
        aug_l[0] = self._rs_prev_l
        aug_r[0] = self._rs_prev_r
        aug_l[1:] = left
        aug_r[1:] = right

        # Output positions: start, start+step, ... while <= L-1 (interpolatable).
        n_out = int(np.floor((L - 1 - start) / step)) + 1
        positions = start + step * np.arange(n_out, dtype=np.float64)
        xq = positions + 1.0                       # into aug index space [0, L]
        xp = np.arange(L + 1, dtype=np.float64)
        out_l = np.interp(xq, xp, aug_l).astype(np.float32)
        out_r = np.interp(xq, xp, aug_r).astype(np.float32)

        # Carry the next position into the next block's index frame (index L → 0).
        self._rs_frac = (start + step * n_out) - L
        self._rs_prev_l = float(left[-1])
        self._rs_prev_r = float(right[-1])
        return out_l, out_r
