import sys
import os
import numpy as np
import pytest
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from capture import AudioCapture, AudioFormat


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


class TestResample:
    def test_mono_to_stereo_doubles_length(self):
        mono = np.ones(512, dtype=np.int16)
        fmt = AudioFormat(sample_rate=44100, channels=1, sample_width=2)
        result_bytes = AudioCapture._resample(mono.tobytes(), fmt)
        result = np.frombuffer(result_bytes, dtype=np.int16)
        assert len(result) == 1024

    def test_mono_to_stereo_both_channels_equal(self):
        mono = (np.arange(256, dtype=np.int16) * 100)
        fmt = AudioFormat(sample_rate=44100, channels=1, sample_width=2)
        result_bytes = AudioCapture._resample(mono.tobytes(), fmt)
        result = np.frombuffer(result_bytes, dtype=np.int16)
        left = result[0::2]
        right = result[1::2]
        np.testing.assert_array_equal(left, right)

    def test_48k_to_44100_reduces_sample_count(self):
        frames_in = 480
        samples_in = np.zeros(frames_in * 2, dtype=np.int16)
        fmt = AudioFormat(sample_rate=48000, channels=2, sample_width=2)
        out = AudioCapture._resample(samples_in.tobytes(), fmt)
        result = np.frombuffer(out, dtype=np.int16)
        expected_frames = int(frames_in * 44100 / 48000)
        assert abs(len(result) // 2 - expected_frames) <= 2

    def test_surround_takes_first_two_channels(self):
        frames = 64
        channels = 6
        samples = np.arange(frames * channels, dtype=np.int16)
        fmt = AudioFormat(sample_rate=44100, channels=6, sample_width=2)
        result_bytes = AudioCapture._resample(samples.tobytes(), fmt)
        result = np.frombuffer(result_bytes, dtype=np.int16)
        assert len(result) == frames * 2


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
