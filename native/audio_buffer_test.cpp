#include "audio_buffer.h"
#include <cassert>
#include <iostream>

int main() {
    AudioBuffer buffer(32);
    Frame input[20];
    for (int i = 0; i < 20; ++i) input[i] = {static_cast<int16_t>(i), static_cast<int16_t>(-i)};
    Frame output[16];
    assert(buffer.push(input, 20));
    assert(buffer.read(output, 16, 0.0));
    for (int i = 0; i < 16; ++i) {
        assert(output[i].left == i);
        assert(output[i].right == -i);
    }
    assert(buffer.push(input, 20));
    assert(!buffer.push(input, 20));
    assert(buffer.size() == 24);
    assert(buffer.read(output, 16, 0.0));
    for (int i = 0; i < 16; ++i) assert(output[i].left == (i < 4 ? i + 16 : i - 4));

    // Both correction directions preserve fractional phase across reads and wraps.
    for (double drift : {-0.004, 0.0, 0.004}) {
        AudioBuffer clock(4096);
        std::vector<Frame> silence(2048);
        Frame block[352];
        uint64_t pushed = 2048;
        assert(clock.push(silence.data(), silence.size()));
        for (int i = 0; i < 10000; ++i) {
            if (clock.size() < 1024) {
                assert(clock.push(silence.data(), silence.size()));
                pushed += silence.size();
            }
            assert(clock.read(block, 352, drift));
            for (const auto& sample : block) assert(sample.left == 0 && sample.right == 0);
        }
        double expected = 10000.0 * 352 * (1.0 + drift);
        assert(std::abs(static_cast<double>(pushed - clock.size()) - expected) < 1.1);
    }
    AudioBuffer ramp(2048);
    std::vector<Frame> line(2000);
    for (int i = 0; i < 2000; ++i) line[i] = {static_cast<int16_t>(i * 10), 0};
    assert(ramp.push(line.data(), line.size()));
    double position = 0;
    for (int i = 0; i < 100; ++i) {
        assert(ramp.read(output, 16, 0.003));
        for (const auto& frame : output) {
            assert(std::abs(frame.left - position * 10) <= 0.51);
            position += 1.003;
        }
    }
    // A ramp and silence cannot expose the high-frequency modulation caused by
    // linear interpolation. Compare a known tone with its ideal resampled phase.
    for (double correction : {-0.0001, 0.0001}) {
        AudioBuffer tone(5003);
        uint64_t produced = 0;
        double phase = 0, error = 0, reference = 0;
        Frame samples[1024], block[352];
        constexpr double omega = 2 * 3.14159265358979323846 * 16000 / 44100;
        for (int packet = 0; packet < 250; ++packet) {
            while (tone.size() < 2048) {
                for (auto& sample : samples) {
                    sample.left = static_cast<int16_t>(std::lround(20000 * std::sin(omega * produced++)));
                    sample.right = -sample.left;
                }
                assert(tone.push(samples, 1024));
            }
            assert(tone.read(block, 352, correction));
            for (const auto& sample : block) {
                double expected = 20000 * std::sin(omega * phase);
                if (phase > 100) {
                    error += std::pow(sample.left - expected, 2);
                    reference += expected * expected;
                    assert(std::abs(sample.left + sample.right) <= 1);
                }
                phase += 1 + correction;
            }
        }
        double relativeError = std::sqrt(error / reference);
        std::cout << "16 kHz drift " << correction * 1000000 << " ppm: RMS error " << relativeError * 100 << "%\n";
        assert(relativeError < 0.002);
    }
    std::cout << "Native buffer: ordering, overflow, wraparound, drift, continuity and spectral fidelity passed\n";
}
