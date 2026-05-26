import asyncio
import struct
import sys
import os
import threading
import pytest
from unittest.mock import MagicMock, patch, AsyncMock
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from raop import (
    RAOPClient,
    _StreamFeeder,
    _streaming_wav_header,
)


class TestStreamingWavHeader:
    def test_length_is_44_bytes(self):
        assert len(_streaming_wav_header()) == 44

    def test_riff_wave_markers(self):
        hdr = _streaming_wav_header()
        assert hdr[:4] == b"RIFF"
        assert hdr[8:12] == b"WAVE"
        assert hdr[12:16] == b"fmt "
        assert hdr[36:40] == b"data"

    def test_pcm_format_tag(self):
        hdr = _streaming_wav_header()
        fmt_tag = struct.unpack_from("<H", hdr, 20)[0]
        assert fmt_tag == 1  # PCM

    def test_sample_rate_encoded(self):
        hdr = _streaming_wav_header(sample_rate=44100, channels=2, bits=16)
        sr = struct.unpack_from("<I", hdr, 24)[0]
        assert sr == 44100

    def test_channels_encoded(self):
        hdr = _streaming_wav_header(channels=2)
        ch = struct.unpack_from("<H", hdr, 22)[0]
        assert ch == 2

    def test_streaming_data_size_placeholder(self):
        hdr = _streaming_wav_header()
        data_size = struct.unpack_from("<I", hdr, 40)[0]
        assert data_size == 0x7FFFFFFF


class TestStreamFeeder:
    def test_readable(self):
        assert _StreamFeeder().readable()

    def test_not_seekable(self):
        assert not _StreamFeeder().seekable()

    def test_feed_header_served_first(self):
        f = _StreamFeeder()
        f.feed_header(b"\x00\x01\x02\x03")
        f.feed(b"\x04\x05\x06\x07")
        buf = bytearray(4)
        n = f.readinto(buf)
        assert n == 4
        assert bytes(buf) == b"\x00\x01\x02\x03"  # header comes first
        n2 = f.readinto(buf)
        assert n2 == 4
        assert bytes(buf) == b"\x04\x05\x06\x07"  # then queue

    def test_feed_header_never_dropped_by_cap(self):
        f = _StreamFeeder()
        f.feed_header(b"\xFF\xFF")
        # Fill queue past the cap — header must survive
        for i in range(f._MAX_QUEUE_CHUNKS + 2):
            f.feed(bytes([i]) * 4)
        buf = bytearray(2)
        n = f.readinto(buf)
        assert n == 2
        assert bytes(buf) == b"\xFF\xFF"  # header still first

    def test_feed_and_readinto_exact(self):
        f = _StreamFeeder()
        f.feed(b"\x01\x02\x03\x04")
        buf = bytearray(4)
        n = f.readinto(buf)
        assert n == 4
        assert bytes(buf) == b"\x01\x02\x03\x04"

    def test_readinto_smaller_than_chunk(self):
        f = _StreamFeeder()
        f.feed(b"\x01\x02\x03\x04")
        buf = bytearray(2)
        n1 = f.readinto(buf)
        assert n1 == 2
        assert bytes(buf) == b"\x01\x02"
        n2 = f.readinto(buf)
        assert n2 == 2
        assert bytes(buf) == b"\x03\x04"

    def test_readinto_larger_than_chunk(self):
        f = _StreamFeeder()
        f.feed(b"\xAA\xBB")
        buf = bytearray(8)
        n = f.readinto(buf)
        assert n == 2
        assert buf[:2] == b"\xAA\xBB"

    def test_close_feed_returns_zero(self):
        f = _StreamFeeder()
        f.close_feed()
        buf = bytearray(4)
        n = f.readinto(buf)
        assert n == 0

    def test_queue_cap_drops_oldest(self):
        f = _StreamFeeder()
        # Fill to cap + 1: oldest chunk should be dropped
        for i in range(f._MAX_QUEUE_CHUNKS + 1):
            f.feed(bytes([i]) * 4)
        assert f._q.qsize() == f._MAX_QUEUE_CHUNKS
        # First readable chunk should be index 1 (index 0 was dropped)
        buf = bytearray(4)
        f.readinto(buf)
        assert bytes(buf) == bytes([1]) * 4

    def test_flush_stale_on_header_consumed(self):
        f = _StreamFeeder()
        f.feed_header(b"\xAA\xBB")
        f.feed(b"\x01\x01")  # stale audio accumulated during setup
        f.feed(b"\x02\x02")
        buf = bytearray(2)
        # Read header — flush fires after header consumed
        f.readinto(buf)
        assert bytes(buf) == b"\xAA\xBB"
        # Queue should be empty; next read blocks waiting for fresh audio
        assert f._q.empty()
        assert f._remainder == b""

    def test_feed_from_thread_read_from_main(self):
        f = _StreamFeeder()

        def writer():
            import time
            time.sleep(0.05)
            f.feed(b"\xFF" * 8)

        t = threading.Thread(target=writer)
        t.start()
        buf = bytearray(8)
        n = f.readinto(buf)
        t.join()
        assert n == 8
        assert buf == b"\xFF" * 8

    def test_multiple_feeds_drained_in_order(self):
        f = _StreamFeeder()
        f.feed(b"\x01\x02")
        f.feed(b"\x03\x04")
        buf1, buf2 = bytearray(2), bytearray(2)
        n1 = f.readinto(buf1)
        n2 = f.readinto(buf2)
        assert n1 == 2 and n2 == 2
        assert bytes(buf1 + buf2) == b"\x01\x02\x03\x04"


class TestRAOPClient:
    def _patched_connect(self, client: RAOPClient, host="192.168.1.1", port=7000):
        """Call client.connect() with all heavy dependencies mocked."""
        mock_loop = MagicMock()
        mock_loop.is_running.return_value = False
        with patch("raop.asyncio.new_event_loop", return_value=mock_loop), \
             patch("raop.FileStorage"), \
             patch("raop.asyncio.run_coroutine_threadsafe"), \
             patch("raop.threading.Thread") as mock_thread_cls:
            mock_thread_cls.return_value = MagicMock()
            client.connect(host, port, volume=60.0)
        return mock_loop

    def test_init_proc_is_none(self):
        c = RAOPClient()
        assert c._proc is None
        assert c._alive is False

    def test_connect_sets_alive_sentinel(self):
        c = RAOPClient()
        self._patched_connect(c)
        assert c._alive is True
        assert c._proc is not None

    def test_connect_creates_feeder_with_wav_header(self):
        c = RAOPClient()
        self._patched_connect(c)
        assert c._feeder is not None
        # WAV header stored in _header (never dropped), served before queue
        buf = bytearray(44)
        n = c._feeder.readinto(buf)
        assert n == 44
        assert buf[:4] == b"RIFF"

    def test_connect_stores_host_port_volume(self):
        c = RAOPClient()
        self._patched_connect(c, host="10.0.0.5", port=1234)
        assert c._host == "10.0.0.5"
        assert c._port == 1234

    def test_send_chunk_feeds_feeder(self):
        c = RAOPClient()
        c._feeder = MagicMock()
        c._alive = True
        data = b"\xDE\xAD" * 64
        c.send_chunk(data)
        c._feeder.feed.assert_called_once_with(data)

    def test_send_chunk_noop_when_not_alive(self):
        c = RAOPClient()
        c._feeder = MagicMock()
        c._alive = False
        c.send_chunk(b"\x00" * 64)
        c._feeder.feed.assert_not_called()

    def test_send_chunk_noop_when_feeder_none(self):
        c = RAOPClient()
        c._alive = True
        c._feeder = None
        c.send_chunk(b"\x00" * 64)  # must not raise

    def test_disconnect_closes_feeder(self):
        c = RAOPClient()
        mock_feeder = MagicMock()
        c._feeder = mock_feeder
        c._alive = True
        c._proc = object()
        mock_loop = MagicMock()
        mock_loop.is_running.return_value = True
        c._loop = mock_loop
        mock_thread = MagicMock()
        c._loop_thread = mock_thread
        c.disconnect()
        mock_feeder.close_feed.assert_called_once()
        assert c._feeder is None
        assert c._proc is None
        assert c._alive is False
        assert c._loop is None

    def test_disconnect_noop_when_not_connected(self):
        c = RAOPClient()
        c.disconnect()  # must not raise

    def test_set_volume_schedules_coroutine_when_alive(self):
        c = RAOPClient()
        mock_atv = MagicMock()
        mock_loop = MagicMock()
        mock_loop.is_running.return_value = True
        c._atv = mock_atv
        c._loop = mock_loop
        c._alive = True
        with patch("raop.asyncio.run_coroutine_threadsafe") as mock_schedule:
            c.set_volume(80.0)
            mock_schedule.assert_called_once()

    def test_set_volume_noop_when_not_alive(self):
        c = RAOPClient()
        with patch("raop.asyncio.run_coroutine_threadsafe") as mock_schedule:
            c.set_volume(80.0)
            mock_schedule.assert_not_called()

    def test_proc_cleared_on_stream_task_failure(self):
        """_proc sentinel goes None when the pyatv stream ends."""
        c = RAOPClient()
        c._alive = True
        c._proc = object()

        async def run():
            await c._stream_task("192.168.1.1", 7000, 50.0)

        with patch("raop.pyatv.scan", new_callable=AsyncMock, return_value=[]), \
             patch("raop.FileStorage"):
            c._storage = MagicMock()
            c._storage.load = AsyncMock()
            asyncio.run(run())

        assert c._proc is None
        assert c._alive is False
