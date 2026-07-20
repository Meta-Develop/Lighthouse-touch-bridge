#include "named_pipe_receiver.hpp"

#include "ltb_driver/peer_session.hpp"
#include "ltb_driver/retry_backoff.hpp"
#include "monotonic_clock.hpp"

#include <windows.h>
#include <sddl.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <span>
#include <string>
#include <vector>

namespace ltb::driver::openvr {
namespace {

constexpr wchar_t kPipeName[] = LR"(\\.\pipe\lighthouse-touch-bridge-v1)";

constexpr std::uint32_t kSetupRetryInitialDelayMs = 1'000;
constexpr std::uint32_t kSetupRetryMaximumDelayMs = 30'000;

void LogPipeSetupFailure(
    const char* stage, DWORD error, std::uint32_t retry_delay_ms) noexcept {
    char message[160];
    std::snprintf(
        message,
        sizeof(message),
        "driver_ltb: named pipe receiver: %s failed (Win32 error %lu); "
        "retrying in %u ms\n",
        stage,
        static_cast<unsigned long>(error),
        static_cast<unsigned>(retry_delay_ms));
    OutputDebugStringA(message);
}

void WaitBeforeRetry(
    std::uint32_t delay_ms, const std::atomic<bool>& stop_requested) noexcept {
    constexpr std::uint32_t kSliceMs = 100;
    std::uint32_t waited_ms = 0;
    while (waited_ms < delay_ms && !stop_requested.load(std::memory_order_acquire)) {
        const std::uint32_t remaining_ms = delay_ms - waited_ms;
        const std::uint32_t slice_ms = remaining_ms < kSliceMs ? remaining_ms : kSliceMs;
        Sleep(static_cast<DWORD>(slice_ms));
        waited_ms += slice_ms;
    }
}

class LocalHandle final {
public:
    explicit LocalHandle(HANDLE value = INVALID_HANDLE_VALUE) noexcept : value_(value) {}
    ~LocalHandle() {
        if (value_ != nullptr && value_ != INVALID_HANDLE_VALUE) {
            CloseHandle(value_);
        }
    }
    LocalHandle(const LocalHandle&) = delete;
    LocalHandle& operator=(const LocalHandle&) = delete;

    HANDLE Get() const noexcept { return value_; }
    bool Valid() const noexcept { return value_ != nullptr && value_ != INVALID_HANDLE_VALUE; }

private:
    HANDLE value_;
};

class LocalMemory final {
public:
    explicit LocalMemory(HLOCAL value = nullptr) noexcept : value_(value) {}
    ~LocalMemory() {
        if (value_ != nullptr) {
            LocalFree(value_);
        }
    }
    LocalMemory(const LocalMemory&) = delete;
    LocalMemory& operator=(const LocalMemory&) = delete;
    LocalMemory(LocalMemory&& other) noexcept : value_(other.value_) {
        other.value_ = nullptr;
    }
    LocalMemory& operator=(LocalMemory&& other) noexcept {
        if (this != &other) {
            if (value_ != nullptr) {
                LocalFree(value_);
            }
            value_ = other.value_;
            other.value_ = nullptr;
        }
        return *this;
    }

    HLOCAL Get() const noexcept { return value_; }

private:
    HLOCAL value_;
};

bool CurrentUserSecurityDescriptor(LocalMemory& owner) noexcept {
    HANDLE raw_token = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw_token) == FALSE) {
        return false;
    }
    const LocalHandle token(raw_token);

    DWORD required = 0;
    GetTokenInformation(token.Get(), TokenUser, nullptr, 0, &required);
    if (required == 0 || GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
        return false;
    }
    std::vector<std::byte> token_buffer(required);
    if (GetTokenInformation(
            token.Get(), TokenUser, token_buffer.data(), required, &required) == FALSE) {
        return false;
    }
    const auto* token_user = reinterpret_cast<const TOKEN_USER*>(token_buffer.data());

    LPWSTR raw_sid = nullptr;
    if (ConvertSidToStringSidW(token_user->User.Sid, &raw_sid) == FALSE) {
        return false;
    }
    [[maybe_unused]] const LocalMemory sid_memory(raw_sid);
    const std::wstring sddl = std::wstring(L"D:P(A;;GA;;;") + raw_sid + L")";

    PSECURITY_DESCRIPTOR raw_descriptor = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.c_str(),
            SDDL_REVISION_1,
            &raw_descriptor,
            nullptr) == FALSE) {
        return false;
    }
    owner = LocalMemory(raw_descriptor);
    return true;
}

bool WaitForIo(
    HANDLE pipe,
    OVERLAPPED& overlapped,
    const std::atomic<bool>& stop_requested,
    DWORD& transferred) noexcept {
    while (!stop_requested.load(std::memory_order_acquire)) {
        const DWORD wait_result = WaitForSingleObject(overlapped.hEvent, 100);
        if (wait_result == WAIT_TIMEOUT) {
            continue;
        }
        if (wait_result != WAIT_OBJECT_0) {
            return false;
        }
        return GetOverlappedResult(pipe, &overlapped, &transferred, FALSE) != FALSE;
    }
    CancelIoEx(pipe, &overlapped);
    WaitForSingleObject(overlapped.hEvent, INFINITE);
    return false;
}

PeerSessionEvidence ResolvePeerSession(HANDLE pipe) noexcept {
    ULONG client_process_id = 0;
    if (GetNamedPipeClientProcessId(pipe, &client_process_id) == FALSE) {
        return {.lookup_status = PeerSessionLookupStatus::ClientProcessLookupFailed};
    }

    DWORD client_session_id = 0;
    if (ProcessIdToSessionId(static_cast<DWORD>(client_process_id), &client_session_id) == FALSE) {
        return {.lookup_status = PeerSessionLookupStatus::ClientSessionLookupFailed};
    }

    DWORD server_session_id = 0;
    if (ProcessIdToSessionId(GetCurrentProcessId(), &server_session_id) == FALSE) {
        return {.lookup_status = PeerSessionLookupStatus::ServerSessionLookupFailed};
    }

    return {
        .lookup_status = PeerSessionLookupStatus::Resolved,
        .client_session_id = client_session_id,
        .server_session_id = server_session_id,
    };
}

}  // namespace

std::uint64_t MonotonicNanoseconds() noexcept {
    LARGE_INTEGER counter{};
    LARGE_INTEGER frequency{};
    if (QueryPerformanceCounter(&counter) == FALSE ||
        QueryPerformanceFrequency(&frequency) == FALSE || frequency.QuadPart <= 0) {
        return 0;
    }
    const auto seconds = static_cast<std::uint64_t>(counter.QuadPart / frequency.QuadPart);
    const auto remainder = static_cast<std::uint64_t>(counter.QuadPart % frequency.QuadPart);
    return seconds * 1'000'000'000ULL +
        remainder * 1'000'000'000ULL / static_cast<std::uint64_t>(frequency.QuadPart);
}

NamedPipeReceiver::NamedPipeReceiver(StateStore& store) noexcept : store_(store) {}

NamedPipeReceiver::~NamedPipeReceiver() {
    Stop();
}

bool NamedPipeReceiver::Start() {
    if (thread_.joinable()) {
        return true;
    }
    stop_requested_.store(false, std::memory_order_release);
    try {
        thread_ = std::thread(&NamedPipeReceiver::Run, this);
    } catch (...) {
        return false;
    }
    return true;
}

void NamedPipeReceiver::Stop() noexcept {
    stop_requested_.store(true, std::memory_order_release);
    if (thread_.joinable()) {
        thread_.join();
    }
}

void NamedPipeReceiver::Run() noexcept {
    RetryBackoff setup_backoff(kSetupRetryInitialDelayMs, kSetupRetryMaximumDelayMs);
    while (!stop_requested_.load(std::memory_order_acquire)) {
        LocalMemory security_descriptor;
        if (!CurrentUserSecurityDescriptor(security_descriptor)) {
            const DWORD error = GetLastError();
            const std::uint32_t retry_delay_ms = setup_backoff.NextDelayMilliseconds();
            LogPipeSetupFailure("security descriptor setup", error, retry_delay_ms);
            WaitBeforeRetry(retry_delay_ms, stop_requested_);
            continue;
        }
        SECURITY_ATTRIBUTES security_attributes{};
        security_attributes.nLength = sizeof(security_attributes);
        security_attributes.lpSecurityDescriptor = security_descriptor.Get();
        security_attributes.bInheritHandle = FALSE;

        const LocalHandle pipe(CreateNamedPipeW(
            kPipeName,
            PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            0,
            4096,
            0,
            &security_attributes));
        if (!pipe.Valid()) {
            const DWORD error = GetLastError();
            const std::uint32_t retry_delay_ms = setup_backoff.NextDelayMilliseconds();
            LogPipeSetupFailure("CreateNamedPipeW", error, retry_delay_ms);
            WaitBeforeRetry(retry_delay_ms, stop_requested_);
            continue;
        }

        const LocalHandle connect_event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!connect_event.Valid()) {
            const DWORD error = GetLastError();
            const std::uint32_t retry_delay_ms = setup_backoff.NextDelayMilliseconds();
            LogPipeSetupFailure("CreateEventW (connect event)", error, retry_delay_ms);
            WaitBeforeRetry(retry_delay_ms, stop_requested_);
            continue;
        }
        setup_backoff.Reset();
        OVERLAPPED connect_overlapped{};
        connect_overlapped.hEvent = connect_event.Get();
        bool connected = ConnectNamedPipe(pipe.Get(), &connect_overlapped) != FALSE;
        if (!connected) {
            const DWORD error = GetLastError();
            if (error == ERROR_PIPE_CONNECTED) {
                connected = true;
            } else if (error == ERROR_IO_PENDING) {
                DWORD ignored = 0;
                connected = WaitForIo(pipe.Get(), connect_overlapped, stop_requested_, ignored);
            }
        }
        if (!connected) {
            continue;
        }

        const PeerSessionPacketGate packet_gate(store_, ResolvePeerSession(pipe.Get()));
        const auto authorization = packet_gate.Authorization();
        if (authorization != PeerSessionAuthorization::Authorized) {
            OutputDebugStringA(PeerSessionAuthorizationDiagnostic(authorization));
            DisconnectNamedPipe(pipe.Get());
            continue;
        }

        while (!stop_requested_.load(std::memory_order_acquire)) {
            std::array<std::uint8_t, kHandStatePacketSize> buffer{};
            const LocalHandle read_event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!read_event.Valid()) {
                break;
            }
            OVERLAPPED read_overlapped{};
            read_overlapped.hEvent = read_event.Get();
            DWORD bytes_read = 0;
            bool read_ok = ReadFile(
                pipe.Get(),
                buffer.data(),
                static_cast<DWORD>(buffer.size()),
                &bytes_read,
                &read_overlapped) != FALSE;
            if (!read_ok) {
                const DWORD error = GetLastError();
                if (error == ERROR_IO_PENDING) {
                    read_ok = WaitForIo(pipe.Get(), read_overlapped, stop_requested_, bytes_read);
                } else if (error == ERROR_MORE_DATA) {
                    break;
                }
            }
            if (!read_ok || bytes_read == 0) {
                break;
            }
            packet_gate.ApplyPacket(
                std::span<const std::uint8_t>(buffer.data(), bytes_read),
                MonotonicNanoseconds());
        }
        DisconnectNamedPipe(pipe.Get());
    }
}

}  // namespace ltb::driver::openvr
