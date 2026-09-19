#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <avrt.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <string>
#include <thread>
#include "audio_buffer.h"

#define API extern "C" __declspec(dllexport)

template<class T> class ComPtr {
public:
    T* value = nullptr;
    ~ComPtr() { if (value) value->Release(); }
    T* operator->() const { return value; }
    void** address() { return reinterpret_cast<void**>(&value); }
};

class Apartment {
public:
    HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~Apartment() { if (SUCCEEDED(result)) CoUninitialize(); }
};

struct Stats {
    uint64_t captured = 0;
    uint64_t delivered = 0;
    uint64_t discontinuities = 0;
    uint64_t overflows = 0;
    uint32_t queued = 0;
    int32_t error = 0;
};

class Engine {
public:
    explicit Engine(const wchar_t* id) : deviceId(id ? id : L""), buffer(44100 * 2) {}
    ~Engine() { stop(); }

    int start() {
        worker = std::thread([this] { run(); });
        std::unique_lock lock(mutex);
        changed.wait(lock, [this] { return initialized; });
        return stats.error;
    }

    void stop() {
        stopping = true;
        changed.notify_all();
        if (worker.joinable()) worker.join();
    }

    int read(Frame* output, uint32_t length, uint32_t timeout) {
        if (!output || !length || length > 44100) return -1;
        std::unique_lock lock(mutex);
        double correction = drift.load();
        changed.wait_for(lock, std::chrono::milliseconds(timeout), [&] {
            return stopping || stats.error || buffer.size() >= buffer.needed(length, correction);
        });
        if (stopping || stats.error) return -1;
        if (!buffer.read(output, length, correction)) return 0;
        stats.delivered += length;
        stats.queued = static_cast<uint32_t>(buffer.size());
        return static_cast<int>(length);
    }

    int prime(uint32_t frames, uint32_t timeout) {
        if (!frames || frames > 44100) return -1;
        std::unique_lock lock(mutex);
        changed.wait_for(lock, std::chrono::milliseconds(timeout), [&] {
            return stopping || stats.error || buffer.size() >= frames;
        });
        if (stopping || stats.error) return -1;
        return buffer.size() >= frames ? 1 : 0;
    }

    Stats snapshot() {
        std::lock_guard lock(mutex);
        return stats;
    }

    std::atomic<double> drift{0.0};

private:
    void finish(HRESULT result) {
        std::lock_guard lock(mutex);
        stats.error = result;
        initialized = true;
        changed.notify_all();
    }

    void run() {
        Apartment apartment;
        if (FAILED(apartment.result)) { finish(apartment.result); return; }
        HRESULT result = capture();
        finish(result);
    }

    HRESULT capture() {
        ComPtr<IMMDeviceEnumerator> enumerator;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator), enumerator.address());
        if (FAILED(hr)) return hr;
        ComPtr<IMMDevice> device;
        hr = deviceId.empty()
            ? enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device.value)
            : enumerator->GetDevice(deviceId.c_str(), &device.value);
        if (FAILED(hr)) return hr;

        WAVEFORMATEX format{};
        format.wFormatTag = WAVE_FORMAT_PCM;
        format.nChannels = 2;
        format.nSamplesPerSec = 44100;
        format.wBitsPerSample = 16;
        format.nBlockAlign = 4;
        format.nAvgBytesPerSec = 44100 * 4;
        constexpr DWORD conversion = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
            | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        ComPtr<IAudioClient> input;
        ComPtr<IAudioClient> silence;
        hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, input.address());
        if (FAILED(hr)) return hr;
        hr = input->Initialize(AUDCLNT_SHAREMODE_SHARED,
            conversion | AUDCLNT_STREAMFLAGS_LOOPBACK, 1000000, 0, &format, nullptr);
        if (FAILED(hr)) return hr;
        ComPtr<IAudioCaptureClient> reader;
        hr = input->GetService(__uuidof(IAudioCaptureClient), reader.address());
        if (FAILED(hr)) return hr;

        // Keep the endpoint clock running during pauses without synthesizing
        // missing capture packets from read timeouts.
        hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, silence.address());
        if (FAILED(hr)) return hr;
        hr = silence->Initialize(AUDCLNT_SHAREMODE_SHARED, conversion,
            1000000, 0, &format, nullptr);
        if (FAILED(hr)) return hr;
        ComPtr<IAudioRenderClient> writer;
        hr = silence->GetService(__uuidof(IAudioRenderClient), writer.address());
        if (FAILED(hr)) return hr;
        UINT32 capacity = 0;
        hr = silence->GetBufferSize(&capacity);
        if (FAILED(hr)) return hr;
        BYTE* data = nullptr;
        hr = writer->GetBuffer(capacity, &data);
        if (FAILED(hr)) return hr;
        hr = writer->ReleaseBuffer(capacity, AUDCLNT_BUFFERFLAGS_SILENT);
        if (FAILED(hr)) return hr;
        hr = input->Start();
        if (FAILED(hr)) return hr;
        hr = silence->Start();
        if (FAILED(hr)) { input->Stop(); return hr; }
        DWORD taskIndex = 0;
        HANDLE priority = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
        finish(S_OK);

        while (!stopping) {
            UINT32 padding = 0;
            hr = silence->GetCurrentPadding(&padding);
            if (FAILED(hr)) break;
            if (padding < capacity) {
                hr = writer->GetBuffer(capacity - padding, &data);
                if (FAILED(hr)) break;
                hr = writer->ReleaseBuffer(capacity - padding, AUDCLNT_BUFFERFLAGS_SILENT);
                if (FAILED(hr)) break;
            }
            UINT32 packet = 0;
            hr = reader->GetNextPacketSize(&packet);
            if (FAILED(hr)) break;
            while (packet && !stopping) {
                UINT32 frames = 0;
                DWORD flags = 0;
                hr = reader->GetBuffer(&data, &frames, &flags, nullptr, nullptr);
                if (FAILED(hr)) break;
                bool accepted;
                {
                    std::lock_guard lock(mutex);
                    accepted = buffer.push(flags & AUDCLNT_BUFFERFLAGS_SILENT
                        ? nullptr : reinterpret_cast<Frame*>(data), frames);
                    if (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) ++stats.discontinuities;
                    if (!accepted) ++stats.overflows;
                    stats.captured += frames;
                    stats.queued = static_cast<uint32_t>(buffer.size());
                }
                hr = reader->ReleaseBuffer(frames);
                changed.notify_all();
                if (FAILED(hr)) break;
                // A stall exceeding two seconds is a failed session, not permission
                // to silently remove samples from the middle of playback.
                if (!accepted) { hr = HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW); break; }
                hr = reader->GetNextPacketSize(&packet);
                if (FAILED(hr)) break;
            }
            if (FAILED(hr)) break;
            std::unique_lock lock(mutex);
            changed.wait_for(lock, std::chrono::milliseconds(5), [this] { return stopping.load(); });
        }
        silence->Stop();
        input->Stop();
        if (priority) AvRevertMmThreadCharacteristics(priority);
        return hr;
    }

    std::wstring deviceId;
    AudioBuffer buffer;
    std::thread worker;
    std::mutex mutex;
    std::condition_variable changed;
    std::atomic<bool> stopping{false};
    bool initialized = false;
    Stats stats;
};

API uint32_t wa_version() { return 2; }
API int wa_prime(Engine* engine, uint32_t frames, uint32_t timeout) {
    return engine ? engine->prime(frames, timeout) : -1;
}
API double wa_clock() {
    LARGE_INTEGER counter, frequency;
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    return static_cast<double>(counter.QuadPart) / frequency.QuadPart;
}
API HANDLE wa_timer_create() {
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, 2, TIMER_ALL_ACCESS);
    return timer ? timer : CreateWaitableTimerW(nullptr, FALSE, nullptr);
}
API void wa_timer_wait(HANDLE timer, double deadline) {
    double remaining = deadline - wa_clock();
    if (remaining <= 0) return;
    LARGE_INTEGER due;
    due.QuadPart = -static_cast<LONGLONG>(remaining * 10000000.0);
    if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE))
        WaitForSingleObject(timer, 100);
}
API void wa_timer_destroy(HANDLE timer) { if (timer) CloseHandle(timer); }
API HANDLE wa_priority_begin() {
    DWORD index = 0;
    return AvSetMmThreadCharacteristicsW(L"Pro Audio", &index);
}
API void wa_priority_end(HANDLE handle) { if (handle) AvRevertMmThreadCharacteristics(handle); }
API void* wa_create(const wchar_t* deviceId) {
    try { return new Engine(deviceId); } catch (...) { return nullptr; }
}
API int wa_start(Engine* engine) {
    if (!engine) return E_POINTER;
    try { return engine->start(); } catch (...) { return E_FAIL; }
}
API void wa_stop(Engine* engine) { if (engine) engine->stop(); }
API void wa_destroy(Engine* engine) { delete engine; }
API int wa_read(Engine* engine, Frame* output, uint32_t frames, uint32_t timeout) {
    return engine ? engine->read(output, frames, timeout) : -1;
}
API void wa_drift(Engine* engine, double drift) {
    if (engine && std::isfinite(drift)) engine->drift = std::clamp(drift, -0.005, 0.005);
}
API void wa_stats(Engine* engine, Stats* output) {
    if (engine && output) *output = engine->snapshot();
}

API int wa_device(uint32_t index, wchar_t* id, uint32_t idSize,
    wchar_t* name, uint32_t nameSize) {
    Apartment apartment;
    if (FAILED(apartment.result) && apartment.result != RPC_E_CHANGED_MODE) return -1;
    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), enumerator.address());
    if (FAILED(hr)) return -1;
    ComPtr<IMMDeviceCollection> devices;
    hr = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices.value);
    if (FAILED(hr)) return -1;
    UINT count = 0;
    hr = devices->GetCount(&count);
    if (FAILED(hr)) return -1;
    if (index >= count) return 0;
    ComPtr<IMMDevice> device;
    hr = devices->Item(index, &device.value);
    if (FAILED(hr)) return -1;
    LPWSTR deviceId = nullptr;
    hr = device->GetId(&deviceId);
    if (FAILED(hr)) return -1;
    bool fits = id && wcslen(deviceId) < idSize;
    if (fits) wcscpy_s(id, idSize, deviceId);
    CoTaskMemFree(deviceId);
    if (!fits) return -1;
    ComPtr<IPropertyStore> properties;
    hr = device->OpenPropertyStore(STGM_READ, &properties.value);
    if (FAILED(hr)) return -1;
    PROPVARIANT value;
    PropVariantInit(&value);
    hr = properties->GetValue(PKEY_Device_FriendlyName, &value);
    fits = SUCCEEDED(hr) && value.vt == VT_LPWSTR && value.pwszVal
        && name && wcslen(value.pwszVal) < nameSize;
    if (fits) wcscpy_s(name, nameSize, value.pwszVal);
    PropVariantClear(&value);
    return fits ? 1 : -1;
}
