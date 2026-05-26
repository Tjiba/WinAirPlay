import logging
import pyaudiowpatch as pyaudio
import numpy as np
from dataclasses import dataclass
from typing import Optional


TARGET_SAMPLE_RATE = 44100
TARGET_CHANNELS = 2
TARGET_SAMPLE_WIDTH = 2  # bytes (int16)
CHUNK_FRAMES = 1024


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
        """Read one chunk of PCM, normalized to 44100 Hz / stereo / int16."""
        raw = self._stream.read(self._chunk_frames, exception_on_overflow=False)
        if self._format and self._format.needs_resample:
            return self._resample(raw, self._format)
        return raw

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

    @staticmethod
    def _resample(data: bytes, fmt: AudioFormat) -> bytes:
        samples = np.frombuffer(data, dtype=np.int16).astype(np.float32)

        if fmt.channels == 1:
            samples = np.repeat(samples, 2)
        elif fmt.channels > 2:
            reshaped = samples.reshape(-1, fmt.channels)
            samples = reshaped[:, :2].flatten().astype(np.float32)

        if fmt.sample_rate != TARGET_SAMPLE_RATE:
            n_frames = len(samples) // 2
            new_n_frames = int(n_frames * TARGET_SAMPLE_RATE / fmt.sample_rate)
            left = samples[0::2]
            right = samples[1::2]
            x_in = np.arange(len(left))
            x_out = np.linspace(0, len(left) - 1, new_n_frames)
            left_r = np.interp(x_out, x_in, left)
            right_r = np.interp(x_out, x_in, right)
            out = np.empty(new_n_frames * 2, dtype=np.float32)
            out[0::2] = left_r
            out[1::2] = right_r
            samples = out

        return np.clip(samples, -32768, 32767).astype(np.int16).tobytes()
