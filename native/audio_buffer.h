#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <vector>

struct Frame {
    int16_t left = 0;
    int16_t right = 0;
};

class AudioBuffer {
public:
    explicit AudioBuffer(size_t capacity) : frames(capacity) { (void)coefficients(); }

    size_t size() const { return count; }
    size_t available() const { return frames.size() - count; }

    bool push(const Frame* input, size_t length) {
        if (length > available()) return false;
        for (size_t i = 0; i < length; ++i) {
            frames[(head + count + i) % frames.size()] = input ? input[i] : Frame{};
        }
        count += length;
        return true;
    }

    size_t needed(size_t length, double drift) const {
        if (!length) return 0;
        size_t lookahead = drift == 0.0 && position == 0.0 ? 1 : taps / 2 + 1;
        return static_cast<size_t>(std::floor(position + (length - 1) * (1.0 + drift))) + lookahead;
    }

    bool read(Frame* output, size_t length, double drift) {
        if (!length || needed(length, drift) > count) return false;
        for (size_t i = 0; i < length; ++i) {
            auto index = static_cast<size_t>(position);
            double fraction = position - index;
            if (fraction == 0.0) {
                output[i] = frames[(head + index) % frames.size()];
            } else {
                const auto& weights = coefficients()[std::min(phases - 1, static_cast<size_t>(fraction * phases))];
                double left = 0, right = 0;
                for (size_t tap = 0; tap < taps; ++tap) {
                    auto offset = static_cast<int64_t>(index + tap) - static_cast<int64_t>(taps / 2 - 1);
                    const auto& sample = offset < 0 ? history[static_cast<size_t>(static_cast<int64_t>(history.size()) + offset)]
                        : frames[(head + static_cast<size_t>(offset)) % frames.size()];
                    left += sample.left * weights[tap];
                    right += sample.right * weights[tap];
                }
                output[i] = {quantize(left), quantize(right)};
            }
            position += 1.0 + drift;
        }
        size_t consumed = static_cast<size_t>(position);
        std::array<Frame, taps / 2> previous;
        for (size_t i = 0; i < previous.size(); ++i) {
            auto offset = static_cast<int64_t>(consumed) - static_cast<int64_t>(previous.size()) + static_cast<int64_t>(i);
            previous[i] = offset < 0 ? history[static_cast<size_t>(static_cast<int64_t>(history.size()) + offset)]
                : frames[(head + static_cast<size_t>(offset)) % frames.size()];
        }
        history = previous;
        head = (head + consumed) % frames.size();
        count -= consumed;
        position -= consumed;
        return true;
    }

private:
    static constexpr size_t taps = 64;
    static constexpr size_t phases = 2048;
    static int16_t quantize(double value) {
        return static_cast<int16_t>(std::clamp(std::lround(value), -32768L, 32767L));
    }
    static const std::vector<std::array<double, taps>>& coefficients() {
        static const auto table = [] {
            constexpr double pi = 3.14159265358979323846;
            std::vector<std::array<double, taps>> result(phases);
            for (size_t phase = 0; phase < phases; ++phase) {
                double sum = 0;
                for (size_t tap = 0; tap < taps; ++tap) {
                    double x = static_cast<double>(tap) - (taps / 2 - 1) - static_cast<double>(phase) / phases;
                    double sinc = std::abs(x) < 1e-12 ? 1.0 : std::sin(pi * x) / (pi * x);
                    double window = 0.42 + 0.5 * std::cos(pi * x / (taps / 2)) + 0.08 * std::cos(2 * pi * x / (taps / 2));
                    result[phase][tap] = sinc * window;
                    sum += result[phase][tap];
                }
                for (auto& weight : result[phase]) weight /= sum;
            }
            return result;
        }();
        return table;
    }
    std::array<Frame, taps / 2> history{};
    std::vector<Frame> frames;
    size_t head = 0;
    size_t count = 0;
    double position = 0.0;
};
