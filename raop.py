import asyncio
import io
import logging
import os
import queue
import struct
import threading
import time
from typing import Optional

import pyatv
from pyatv.const import Protocol
from pyatv.storage.file_storage import FileStorage
from pyatv.protocols.raop.protocols import StreamContext as _StreamContext
# --- Adjustable RAOP latency -------------------------------------------------
# pyatv's default is 1.5s (66150 samples). This is the constant capture→playback
# offset: lower = tighter A/V sync but a smaller device jitter buffer, so on a
# weak/saturated Wi-Fi it can underrun and crackle. AirPlay 2's advertised floor
# is latencyMin = 11025 (250ms); going under is out-of-spec but works (the old
# build ran at 25ms) — now that capture feeds a clean real-time stream the device
# only has to absorb NETWORK jitter, so lower values are viable again. Exposed as
# a UI slider so the user can dial in their network's floor empirically.
# The monkeypatch reads the module global at each StreamContext.reset() (called on
# stream setup), so a new/restarted connection picks up the current value live.
_RAOP_LATENCY_MS_DEFAULT = 150.0
_RAOP_LATENCY_MS_MIN     = 20.0    # ~ the old aggressive setting; expect glitches
_RAOP_LATENCY_MS_MAX     = 500.0   # very safe, ~half a second of buffer

def _ms_to_samples(ms: float) -> int:
    return max(1, int(round(ms / 1000.0 * 44100)))

_raop_latency_samples = _ms_to_samples(_RAOP_LATENCY_MS_DEFAULT)


def set_raop_latency_ms(ms: float) -> None:
    """Set the RAOP latency (clamped). Takes effect on the next (re)connect."""
    global _raop_latency_samples
    ms = max(_RAOP_LATENCY_MS_MIN, min(_RAOP_LATENCY_MS_MAX, float(ms)))
    _raop_latency_samples = _ms_to_samples(ms)


def get_raop_latency_ms() -> float:
    return _raop_latency_samples / 44100.0 * 1000.0


_orig_sc_reset = _StreamContext.reset

def _low_latency_reset(self) -> None:
    _orig_sc_reset(self)
    self.latency = _raop_latency_samples

_StreamContext.reset = _low_latency_reset


STORAGE_PATH = os.path.join(
    os.environ.get("APPDATA", os.path.expanduser("~")),
    "WinAirPlay",
    "pyatv_storage.json",
)


class _StreamFeeder(io.RawIOBase):
    """Thread-safe blocking RawIOBase fed by a queue — bridges live PCM to pyatv."""

    # Safety valve bounding end-to-end latency. With capture fixed to produce at
    # exactly real time (no phantom silence frames), the queue normally hovers near
    # empty; this only trips on genuine clock drift (PC capture clock vs device DAC,
    # ~100ppm → minutes/hours apart) or a transient stall. When hit we drain the
    # OLDEST chunks down to _DRAIN_TARGET_CHUNKS, snapping latency back low in one
    # clean resync rather than letting it grow unbounded (the old 4.6s cap meant
    # steady-state latency oscillated 2.3–4.6s = the "audio keeps falling behind").
    _MAX_QUEUE_CHUNKS = 72       # ~1.5s of audio
    _DRAIN_TARGET_CHUNKS = 10    # ~0.2s — keep the freshest audio

    def __init__(self, name: str = "?") -> None:
        super().__init__()
        self._header: bytes = b""  # served before queue; never dropped
        self._q: "queue.Queue[Optional[bytes]]" = queue.Queue()
        self._remainder = b""
        # --- DIAGNOSTIC (clock-drift glitch investigation) ---
        # Throttled telemetry: prove whether the 32-chunk flush fires and when.
        self._name = name
        self._t0 = time.monotonic()
        self._last_report = self._t0
        self._peak_qsize = 0
        self._flush_count = 0

    def feed_header(self, data: bytes) -> None:
        """Feed the WAV header — always served first, never affected by the queue cap."""
        self._header = data

    def feed(self, data: bytes) -> None:
        qs = self._q.qsize()
        # --- DIAGNOSTIC telemetry (throttled: ~1 line / 30s on the audio thread) ---
        if qs > self._peak_qsize:
            self._peak_qsize = qs
        now = time.monotonic()
        if now - self._last_report >= 30.0:
            logging.info(
                "[Feeder %s] queue peak=%d/%d over last 30s | flushes so far=%d | %.0fs since connect",
                self._name, self._peak_qsize, self._MAX_QUEUE_CHUNKS,
                self._flush_count, now - self._t0,
            )
            self._peak_qsize = qs
            self._last_report = now

        if qs >= self._MAX_QUEUE_CHUNKS:
            self._flush_count += 1
            target = self._DRAIN_TARGET_CHUNKS
            drained = 0
            while self._q.qsize() > target:
                try:
                    self._q.get_nowait()
                    drained += 1
                except queue.Empty:
                    break
            self._remainder = b""
            logging.warning(
                "[Feeder %s] QUEUE FULL (%d chunks) — drained %d to %d "
                "(flush #%d, %.0fs since connect)",
                self._name, qs, drained, target, self._flush_count, now - self._t0,
            )
        self._q.put(data)

    def close_feed(self) -> None:
        self._header = b""
        self._q.put(None)  # EOF sentinel

    def readable(self) -> bool:
        return True

    def seekable(self) -> bool:
        return False

    def readinto(self, b: bytearray) -> int:  # type: ignore[override]
        # Serve WAV header bytes first (protected, never in queue)
        if self._header:
            n = min(len(b), len(self._header))
            b[:n] = self._header[:n]
            self._header = self._header[n:]
            if not self._header:
                # Header fully consumed — flush any audio that accumulated during
                # pyatv's connection/setup phase so we start on the freshest PCM.
                self._flush_stale()
            return n
        while not self._remainder:
            chunk = self._q.get()
            if chunk is None:
                return 0  # EOF
            self._remainder = chunk
        n = min(len(b), len(self._remainder))
        b[:n] = self._remainder[:n]
        self._remainder = self._remainder[n:]
        return n

    def _flush_stale(self) -> None:
        """Discard all pre-buffered audio — called once when WAV header is consumed."""
        self._remainder = b""
        while not self._q.empty():
            try:
                self._q.get_nowait()
            except queue.Empty:
                break


def _streaming_wav_header(
    sample_rate: int = 44100, channels: int = 2, bits: int = 16
) -> bytes:
    """44-byte WAV header with data-size = 0x7FFFFFFF (streaming WAV)."""
    data_size = 0x7FFFFFFF
    byte_rate = sample_rate * channels * (bits // 8)
    block_align = channels * (bits // 8)
    return struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF", data_size + 36, b"WAVE",
        b"fmt ", 16, 1, channels, sample_rate,
        byte_rate, block_align, bits,
        b"data", data_size,
    )


class RAOPClient:
    """AirPlay 2 audio sender using pyatv (pair-verify + ALAC/AAC streaming)."""

    # Class-level cache: host → pyatv config.  Avoids the 5-10s mDNS scan on
    # every reconnect; the first connection scans, subsequent ones connect directly.
    _config_cache: dict = {}

    def __init__(self) -> None:
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._loop_thread: Optional[threading.Thread] = None
        self._storage: Optional[FileStorage] = None
        self._atv = None
        self._feeder: Optional[_StreamFeeder] = None
        self._alive = False
        # Non-None sentinel: main.py checks this to detect a dead stream
        self._proc: Optional[object] = None
        # Set just before stream_file() starts — lets _audio_loop delay capture
        self._ready = threading.Event()

        self._host: Optional[str] = None
        self._port: Optional[int] = None
        self._volume: float = 50.0

    # ------------------------------------------------------------------ public

    def connect(
        self,
        host: str,
        port: int,
        volume: float = 50.0,
        et: str = "",
        md: str = "",
    ) -> None:
        self._host = host
        self._port = port
        self._volume = volume

        self._ready = threading.Event()
        self._feeder = _StreamFeeder(name=host)
        # WAV header in protected buffer — never dropped by the queue cap
        self._feeder.feed_header(_streaming_wav_header())

        loop = asyncio.new_event_loop()
        self._loop = loop  # publish after creation; local ref is race-safe

        # FileStorage persists pairing credentials between app restarts
        os.makedirs(os.path.dirname(STORAGE_PATH), exist_ok=True)
        self._storage = FileStorage(STORAGE_PATH, loop)

        self._loop_thread = threading.Thread(
            target=self._run_loop, daemon=True, name="pyatv-loop"
        )
        self._loop_thread.start()

        self._alive = True
        self._proc = object()  # non-None = alive

        # Use local `loop` — concurrent disconnect() may null self._loop mid-flight
        asyncio.run_coroutine_threadsafe(
            self._stream_task(host, port, volume), loop
        )
        logging.info("[PyATV] Connecting to %s:%d", host, port)

    def send_chunk(self, pcm_data: bytes) -> None:
        if self._feeder and self._alive:
            self._feeder.feed(pcm_data)

    def set_volume(self, volume_pct: float) -> None:
        self._volume = volume_pct
        # pyatv Audio.set_volume() takes 0–100 (percent), not 0.0–1.0
        if self._atv and self._loop:
            asyncio.run_coroutine_threadsafe(
                self._set_volume_async(volume_pct), self._loop
            )

    def disconnect(self) -> None:
        self._alive = False
        self._ready.set()  # unblock any _audio_loop waiting on _ready
        loop = self._loop
        if self._feeder:
            self._feeder.close_feed()  # sends EOF → stream_file() returns → finally stops loop
            self._feeder = None
        if self._loop_thread:
            self._loop_thread.join(timeout=8)  # wait for _stream_task finally to stop the loop
            if self._loop_thread.is_alive() and loop is not None:
                # pyatv teardown hung past the timeout. Force the event loop to
                # stop so the thread exits, instead of lingering as a zombie that
                # keeps heartbeating/streaming to the device.
                logging.warning("[PyATV] disconnect: loop still alive after 8s — forcing stop")
                try:
                    loop.call_soon_threadsafe(loop.stop)
                except Exception:
                    pass
                self._loop_thread.join(timeout=3)
        # Release the loop's selector + any sockets pyatv left open. A loop that
        # is stopped but never closed leaks an IOCP/epoll handle plus its FDs;
        # repeated across reconnects this exhausts system handles and surfaces as
        # "[WinError 1450] insufficient system resources" — which kills the live
        # stream and spins up an endless reconnect storm.
        thread_alive = bool(self._loop_thread and self._loop_thread.is_alive())
        if loop is not None and not thread_alive and not loop.is_running():
            self._close_loop(loop)
        self._loop_thread = None
        self._loop = None
        self._storage = None
        self._atv = None
        self._proc = None

    # ------------------------------------------------------------------ private

    @staticmethod
    def _close_loop(loop: asyncio.AbstractEventLoop) -> None:
        """Cancel leftover tasks and close a stopped loop to free its FDs/handles."""
        if loop.is_closed():
            return
        try:
            pending = [t for t in asyncio.all_tasks(loop) if not t.done()]
            for t in pending:
                t.cancel()
            if pending:
                loop.run_until_complete(asyncio.gather(*pending, return_exceptions=True))
            loop.run_until_complete(loop.shutdown_asyncgens())
        except Exception as e:
            logging.debug("[PyATV] loop cleanup: %s", e)
        finally:
            try:
                loop.close()
            except Exception:
                pass

    def _run_loop(self) -> None:
        asyncio.set_event_loop(self._loop)
        self._loop.run_forever()

    async def _set_volume_async(self, normalized: float) -> None:
        try:
            await self._atv.audio.set_volume(normalized)
        except Exception as e:
            logging.warning("[PyATV] set_volume failed: %s", e)

    async def _stream_task(self, host: str, port: int, volume: float) -> None:
        try:
            await self._storage.load()

            conf = RAOPClient._config_cache.get(host)
            if conf is not None:
                logging.info("[PyATV] Connecting (cached): %s", conf.name)
                try:
                    self._atv = await pyatv.connect(conf, self._loop, storage=self._storage)
                except Exception as e:
                    logging.warning("[PyATV] Cached connect failed (%s) — rescanning", e)
                    RAOPClient._config_cache.pop(host, None)
                    conf = None

            if conf is None:
                logging.info("[PyATV] Scanning for device at %s ...", host)
                atvs = await pyatv.scan(
                    self._loop, hosts=[host], timeout=5, storage=self._storage
                )
                if not atvs:
                    logging.error("[PyATV] No AirPlay device found at %s", host)
                    return
                conf = atvs[0]
                RAOPClient._config_cache[host] = conf
                logging.info("[PyATV] Found: %s (%s)", conf.name, conf.address)
                self._atv = await pyatv.connect(conf, self._loop, storage=self._storage)

            logging.info("[PyATV] Connected — streaming audio to %s", conf.name)

            await self._storage.save()

            # Set initial volume
            try:
                await self._atv.audio.set_volume(volume)
            except Exception as e:
                logging.debug("[PyATV] set_volume on connect skipped: %s", e)

            self._ready.set()  # audio loop may now start capturing
            # Smaller prefetch buffer = pyatv starts decoding after ~1 chunk instead
            # of waiting to fill 32KB (~185ms) — shaves startup latency on connect.
            buffered = io.BufferedReader(self._feeder, buffer_size=8192)
            await self._atv.stream.stream_file(buffered)

        except Exception:
            logging.exception("[PyATV] Stream error")
        finally:
            self._alive = False
            self._proc = None  # signals main.py audio loop that we stopped
            self._ready.set()  # unblock if we failed before setting it above
            if self._atv:
                try:
                    await self._atv.close()
                except Exception:
                    pass
                self._atv = None
            # Stop the loop only after pyatv has fully cleaned up, so teardown
            # callbacks (transport.close → loop.call_soon) don't hit a closed loop.
            if self._loop and self._loop.is_running():
                self._loop.call_soon_threadsafe(self._loop.stop)
            logging.info("[PyATV] Stream ended")

    def __del__(self) -> None:
        try:
            self.disconnect()
        except Exception:
            pass
