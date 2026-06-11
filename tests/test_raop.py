import asyncio
import struct
import threading
import pytest
from unittest.mock import MagicMock, patch, AsyncMock

from winairplay.raop import (
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
        f.feed(b"\x04\x05\x06\x07")  # stale audio buffered during pyatv setup
        buf = bytearray(4)
        n = f.readinto(buf)
        assert n == 4
        assert bytes(buf) == b"\x00\x01\x02\x03"  # header comes first
        # Consuming the header fires _flush_stale(), which intentionally drops the
        # pre-handshake audio so streaming starts on the freshest PCM. Audio fed
        # AFTER the header is what actually flows.
        f.feed(b"\x08\x09\x0a\x0b")
        n2 = f.readinto(buf)
        assert n2 == 4
        assert bytes(buf) == b"\x08\x09\x0a\x0b"

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

    def test_eof_is_sticky_after_close(self):
        """EOF must be sticky: once close_feed() signals EOF, EVERY subsequent
        readinto returns 0 immediately and never blocks. pyatv's miniaudio decode
        pipeline re-probes the source after the first EOF; the old code enqueued a
        single None sentinel, so the 2nd read blocked forever on the empty queue —
        stream_file() never returned and disconnect() always hit its 8s force-stop
        timeout (the universal '[PyATV] disconnect: loop still alive after 8s')."""
        f = _StreamFeeder()
        f.close_feed()
        buf = bytearray(4)
        assert f.readinto(buf) == 0  # first read consumes the EOF sentinel

        # Second read must NOT block. Run it in a thread and assert it completes.
        result = {}
        t = threading.Thread(target=lambda: result.__setitem__("n", f.readinto(buf)))
        t.start()
        t.join(timeout=2.0)
        assert not t.is_alive(), "readinto blocked after EOF — feeder deadlock"
        assert result["n"] == 0

    def test_eof_sticky_after_draining_real_audio(self):
        """EOF stays sticky even after real audio was streamed then close_feed()."""
        f = _StreamFeeder()
        f.feed(b"\x01\x02\x03\x04")
        f.close_feed()
        buf = bytearray(4)
        assert f.readinto(buf) == 4   # real chunk
        assert f.readinto(buf) == 0   # EOF sentinel
        result = {}
        t = threading.Thread(target=lambda: result.__setitem__("n", f.readinto(buf)))
        t.start()
        t.join(timeout=2.0)
        assert not t.is_alive(), "readinto blocked after EOF — feeder deadlock"
        assert result["n"] == 0

    def test_queue_cap_drains_oldest_to_target(self):
        f = _StreamFeeder()
        extra = 5
        n = f._MAX_QUEUE_CHUNKS + extra
        for i in range(n):
            f.feed(bytes([i % 256]) * 4)
        # Overflow drains the OLDEST down to the drain target (bounding latency),
        # then the post-overflow feeds accumulate on top — never exceeding the cap.
        assert f._q.qsize() <= f._MAX_QUEUE_CHUNKS
        assert f._q.qsize() == f._DRAIN_TARGET_CHUNKS + extra
        # Freshest audio is kept: the first surviving chunk is the one right after
        # the drained span (cap - target), and the very last fed chunk is retained.
        first_surviving = f._MAX_QUEUE_CHUNKS - f._DRAIN_TARGET_CHUNKS
        buf = bytearray(4)
        f.readinto(buf)
        assert bytes(buf) == bytes([first_surviving % 256]) * 4

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


class TestPyatvPatchTargets:
    """winairplay.raop.py monkeypatches pyatv internals to reach sub-250ms latency. These
    guards fail loudly if a pyatv upgrade renames/moves the patched symbols —
    instead of the latency slider silently turning into a no-op."""

    def test_stream_context_reset_is_patched(self):
        from pyatv.protocols.raop.protocols import StreamContext
        from winairplay import raop as raop_mod
        assert StreamContext.reset is raop_mod._low_latency_reset
        assert callable(raop_mod._orig_sc_reset)

    def test_rtsp_setup_is_patched(self):
        from pyatv.support.rtsp import RtspSession
        from winairplay import raop as raop_mod
        assert RtspSession.setup is raop_mod._patched_rtsp_setup
        assert callable(raop_mod._orig_rtsp_setup)

    def test_reset_applies_configured_latency(self):
        from pyatv.protocols.raop.protocols import StreamContext
        from winairplay import raop as raop_mod
        ctx = StreamContext()
        ctx.reset()
        assert ctx.latency == raop_mod._raop_latency_samples


class TestRAOPClientPublicAPI:
    def test_is_alive_reflects_internal_flag(self):
        c = RAOPClient()
        assert c.is_alive is False
        c._alive = True
        assert c.is_alive is True

    def test_is_streaming_requires_alive_and_proc(self):
        c = RAOPClient()
        assert c.is_streaming is False
        c._alive = True
        assert c.is_streaming is False   # no stream task yet
        c._proc = object()
        assert c.is_streaming is True

    def test_wait_ready_true_once_ready(self):
        c = RAOPClient()
        c._ready.set()
        assert c.wait_ready(timeout=0.1) is True

    def test_wait_ready_times_out_false(self):
        c = RAOPClient()
        assert c.wait_ready(timeout=0.05) is False

    def test_no_del_hook(self):
        """__del__ joined threads (up to ~11s) during GC/interpreter shutdown —
        explicit disconnect paths (quit/evict/atexit) cover every case."""
        assert "__del__" not in RAOPClient.__dict__


class TestRAOPClient:
    def _patched_connect(self, client: RAOPClient, host="192.168.1.1", port=7000):
        """Call client.connect() with all heavy dependencies mocked."""
        mock_loop = MagicMock()
        mock_loop.is_running.return_value = False
        with patch("winairplay.raop.asyncio.new_event_loop", return_value=mock_loop), \
             patch("winairplay.raop.FileStorage"), \
             patch("winairplay.raop.asyncio.run_coroutine_threadsafe"), \
             patch("winairplay.raop.threading.Thread") as mock_thread_cls:
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

    def test_connection_lost_marks_dead_and_unblocks_feeder(self):
        """The pyatv DeviceListener callback must flag the client dead (so the audio
        loop reconnects) and EOF the feeder (so the blocked stream_file returns) —
        the fix for 'device dropped us but we kept streaming into the void'."""
        c = RAOPClient()
        c._alive = True
        c._proc = object()
        feeder = MagicMock()
        c._feeder = feeder
        c.connection_lost(Exception("boom"))
        assert c._alive is False
        assert c._proc is None
        feeder.close_feed.assert_called_once()

    def test_connection_closed_marks_dead(self):
        c = RAOPClient()
        c._alive = True
        c._proc = object()
        c._feeder = None
        c.connection_closed()  # must not raise even without a feeder
        assert c._alive is False
        assert c._proc is None

    def test_set_volume_noop_when_dead(self):
        """A blocked/dead connection must not schedule volume coroutines (the flood
        that starved audio pacing). Guarded by _alive."""
        c = RAOPClient()
        c._atv = MagicMock()
        c._loop = MagicMock()
        c._alive = False
        with patch("winairplay.raop.asyncio.run_coroutine_threadsafe") as run:
            c.set_volume(42.0)
            run.assert_not_called()

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
        with patch("winairplay.raop.asyncio.run_coroutine_threadsafe") as mock_schedule:
            c.set_volume(80.0)
            mock_schedule.assert_called_once()

    def test_set_volume_noop_when_not_alive(self):
        c = RAOPClient()
        with patch("winairplay.raop.asyncio.run_coroutine_threadsafe") as mock_schedule:
            c.set_volume(80.0)
            mock_schedule.assert_not_called()

    def test_proc_cleared_on_stream_task_failure(self):
        """_proc sentinel goes None when the pyatv stream ends."""
        c = RAOPClient()
        c._alive = True
        c._proc = object()

        async def run():
            await c._stream_task("192.168.1.1", 7000, 50.0)

        with patch("winairplay.raop.pyatv.scan", new_callable=AsyncMock, return_value=[]), \
             patch("winairplay.raop.FileStorage"):
            c._storage = MagicMock()
            c._storage.load = AsyncMock()
            asyncio.run(run())

        assert c._proc is None
        assert c._alive is False
