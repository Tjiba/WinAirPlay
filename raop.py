import asyncio
import io
import logging
import os
import queue
import struct
import threading
from typing import Optional

import pyatv
from pyatv.const import Protocol
from pyatv.storage.file_storage import FileStorage
from pyatv.protocols.raop.protocols import StreamContext as _StreamContext
# Reduce pyatv's default RAOP latency from 1.5s (66150 samples) to 25ms.
_orig_sc_reset = _StreamContext.reset

def _low_latency_reset(self) -> None:
    _orig_sc_reset(self)
    self.latency = 1102  # 25ms at 44100Hz

_StreamContext.reset = _low_latency_reset


STORAGE_PATH = os.path.join(
    os.environ.get("APPDATA", os.path.expanduser("~")),
    "WinAirPlay",
    "pyatv_storage.json",
)


class _StreamFeeder(io.RawIOBase):
    """Thread-safe blocking RawIOBase fed by a queue — bridges live PCM to pyatv."""

    # Cap on PCM audio chunks. miniaudio's internal buffer oscillates and can
    # accumulate ~10 chunks per cycle; 32 gives headroom without dropping during
    # normal operation. Only clock-drift (rare) causes actual drops.
    _MAX_QUEUE_CHUNKS = 32

    def __init__(self) -> None:
        super().__init__()
        self._header: bytes = b""  # served before queue; never dropped
        self._q: "queue.Queue[Optional[bytes]]" = queue.Queue()
        self._remainder = b""

    def feed_header(self, data: bytes) -> None:
        """Feed the WAV header — always served first, never affected by the queue cap."""
        self._header = data

    def feed(self, data: bytes) -> None:
        if self._q.qsize() >= self._MAX_QUEUE_CHUNKS:
            # Clock drift: flush the whole backlog in one shot (one clean gap)
            # rather than dropping one chunk at a time (repeated pops).
            self._remainder = b""
            while not self._q.empty():
                try:
                    self._q.get_nowait()
                except queue.Empty:
                    break
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
        self._feeder = _StreamFeeder()
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
        if self._feeder:
            self._feeder.close_feed()  # sends EOF → stream_file() returns → finally stops loop
            self._feeder = None
        if self._loop_thread:
            self._loop_thread.join(timeout=8)  # wait for _stream_task finally to stop the loop
        self._loop_thread = None
        self._loop = None
        self._storage = None
        self._atv = None
        self._proc = None

    # ------------------------------------------------------------------ private

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
                    self._loop, hosts=[host], timeout=10, storage=self._storage
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
            buffered = io.BufferedReader(self._feeder, buffer_size=1024)
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
