import numpy as np
import pytest

from winairplay.capture import AudioCapture, AudioFormat


class TestAudioFormat:
    def test_needs_resample_false_when_already_target(self):
        fmt = AudioFormat(sample_rate=44100, channels=2, sample_width=2)
        assert not fmt.needs_resample

    def test_needs_resample_true_different_rate(self):
        fmt = AudioFormat(sample_rate=48000, channels=2, sample_width=2)
        assert fmt.needs_resample

    def test_needs_resample_true_mono(self):
        fmt = AudioFormat(sample_rate=44100, channels=1, sample_width=2)
        assert fmt.needs_resample

    def test_needs_resample_true_surround(self):
        fmt = AudioFormat(sample_rate=44100, channels=6, sample_width=2)
        assert fmt.needs_resample


def _make_capture(fmt: AudioFormat) -> AudioCapture:
    """Build an AudioCapture for resampler tests WITHOUT opening any audio device
    (object.__new__ skips PyAudio init). _resample reads self._format + carry state."""
    cap = object.__new__(AudioCapture)
    cap._format = fmt
    cap._chunk_frames = 1024
    cap._reset_resampler()
    return cap


class TestSetDeviceIndex:
    def test_set_device_index_updates_lookup_target(self):
        cap = object.__new__(AudioCapture)
        cap._device_index = None
        cap.set_device_index(4)
        assert cap._device_index == 4
        cap.set_device_index(None)
        assert cap._device_index is None


class TestResample:
    def test_mono_to_stereo_doubles_length(self):
        mono = np.ones(512, dtype=np.int16)
        fmt = AudioFormat(sample_rate=44100, channels=1, sample_width=2)
        result_bytes = _make_capture(fmt)._resample(mono.tobytes())
        result = np.frombuffer(result_bytes, dtype=np.int16)
        assert len(result) == 1024

    def test_mono_to_stereo_both_channels_equal(self):
        mono = (np.arange(256, dtype=np.int16) * 100)
        fmt = AudioFormat(sample_rate=44100, channels=1, sample_width=2)
        result_bytes = _make_capture(fmt)._resample(mono.tobytes())
        result = np.frombuffer(result_bytes, dtype=np.int16)
        left = result[0::2]
        right = result[1::2]
        np.testing.assert_array_equal(left, right)

    def test_48k_to_44100_reduces_sample_count(self):
        frames_in = 480
        samples_in = np.zeros(frames_in * 2, dtype=np.int16)
        fmt = AudioFormat(sample_rate=48000, channels=2, sample_width=2)
        out = _make_capture(fmt)._resample(samples_in.tobytes())
        result = np.frombuffer(out, dtype=np.int16)
        expected_frames = int(frames_in * 44100 / 48000)
        assert abs(len(result) // 2 - expected_frames) <= 2

    def test_surround_takes_first_two_channels(self):
        frames = 64
        channels = 6
        samples = np.arange(frames * channels, dtype=np.int16)
        fmt = AudioFormat(sample_rate=44100, channels=6, sample_width=2)
        result_bytes = _make_capture(fmt)._resample(samples.tobytes())
        result = np.frombuffer(result_bytes, dtype=np.int16)
        assert len(result) == frames * 2

    def test_resample_rate_is_exact_over_many_chunks(self):
        """The continuous resampler must hold the exact average rate across chunk
        boundaries (the per-chunk np.interp it replaced lost ~0.8 frame/chunk =
        slow drift)."""
        fmt = AudioFormat(sample_rate=48000, channels=2, sample_width=2)
        cap = _make_capture(fmt)
        chunk = 1024
        total_out = 0
        n_chunks = 300
        for _ in range(n_chunks):
            block = np.zeros(chunk * 2, dtype=np.int16).tobytes()
            total_out += len(cap._resample(block)) // 4
        expected = n_chunks * chunk * 44100 / 48000
        assert abs(total_out - expected) <= 1

    def test_resample_continuous_across_boundaries(self):
        """A pure ramp split across chunks must stay monotonic through the join —
        a per-chunk grid reset would create a visible step at each boundary."""
        fmt = AudioFormat(sample_rate=48000, channels=2, sample_width=2)
        cap = _make_capture(fmt)
        chunk = 1024
        # Rising ramp shared across two consecutive chunks (stereo, equal L/R).
        ramp = np.arange(chunk * 2, dtype=np.int16)
        out = []
        for c in range(2):
            block = np.repeat(ramp[c * chunk:(c + 1) * chunk], 2).astype(np.int16)
            out.append(np.frombuffer(cap._resample(block.tobytes()), dtype=np.int16)[0::2])
        joined = np.concatenate(out).astype(np.int64)
        # Strictly non-decreasing (monotonic ramp) → no boundary step-back.
        assert np.all(np.diff(joined) >= 0)


class TestReadChunkStreamRace:
    def test_read_chunk_raises_oserror_when_stream_none(self):
        """A concurrent stop() sets self._stream = None. read_chunk must surface a
        clean OSError (which the audio loop catches + restarts) instead of crashing
        with AttributeError: 'NoneType' has no attribute 'get_read_available' — the
        race that killed the whole audio loop in the field logs."""
        cap = object.__new__(AudioCapture)
        cap._stream = None
        cap._chunk_frames = 1024
        cap._format = None
        cap._in_silence = False
        cap._silence_since = 0.0
        with pytest.raises(OSError):
            cap.read_chunk()


class _FakeStream:
    """Minimal WASAPI-stream stand-in: serves a fixed backlog of available frames."""
    def __init__(self, avail_frames: int):
        self._avail = avail_frames
        self.reads: list = []

    def get_read_available(self) -> int:
        return self._avail

    def read(self, n, exception_on_overflow=False) -> bytes:
        self.reads.append(n)
        self._avail -= n
        return b"\x00" * (n * 2 * 2)  # stereo int16


class TestLiveResync:
    def _cap(self, stream) -> AudioCapture:
        cap = object.__new__(AudioCapture)
        cap._stream = stream
        cap._chunk_frames = 1024
        cap._format = None          # no resample → returns raw chunk
        cap._in_silence = False
        cap._silence_since = 0.0
        return cap

    def test_backlog_is_dropped_to_stay_live(self):
        """A multi-chunk backlog must be discarded down to the freshest chunk so the
        producer never bursts stale audio into the feeder (the startup-glitch fix)."""
        stream = _FakeStream(avail_frames=10 * 1024)  # 10 chunks queued
        out = self._cap(stream).read_chunk()
        assert len(out) == 1024 * 2 * 2            # one chunk returned
        # First read drops 9 stale chunks, second read returns 1 fresh chunk.
        assert stream.reads == [9 * 1024, 1024]

    def test_no_drop_when_buffer_near_empty(self):
        """Steady state (≤1 chunk available) must read exactly one chunk, no drops —
        otherwise we'd inject artifacts during normal real-time playback."""
        stream = _FakeStream(avail_frames=1024)
        out = self._cap(stream).read_chunk()
        assert len(out) == 1024 * 2 * 2
        assert stream.reads == [1024]              # single clean read, no resync drop

    def test_resume_drops_stale_subchunk_backlog(self):
        """On resume from a silence gap, even a SUB-2-chunk backlog is stale (the
        pre-gap tail) and must be dropped — replaying it after the injected silence
        is the crackle heard when switching video/tab. avail=1500 (<2 chunks) would
        NOT trigger the plain backlog path, but the resume path must."""
        cap = self._cap(_FakeStream(avail_frames=1500))
        cap._in_silence = True                     # we were in a silence gap
        cap._silence_since = 0.0
        out = cap.read_chunk()
        assert len(out) == 1024 * 2 * 2
        assert cap._stream.reads == [1500 - 1024, 1024]  # drop stale, keep freshest
        assert cap._in_silence is False            # resumed


class TestFindLoopbackDevice:
    def test_returns_dict_or_none(self):
        cap = AudioCapture()
        result = cap._find_loopback()
        cap.terminate()
        assert result is None or isinstance(result, dict)

    def test_dict_has_required_keys(self):
        cap = AudioCapture()
        result = cap._find_loopback()
        cap.terminate()
        if result is not None:
            assert 'index' in result
            assert 'name' in result
            assert 'defaultSampleRate' in result
            assert result.get('isLoopbackDevice') is True
