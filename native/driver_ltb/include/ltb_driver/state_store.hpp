#pragma once

#include "ltb_driver/protocol.hpp"

#include <array>
#include <cstdint>
#include <mutex>
#include <span>

namespace ltb::driver {

inline constexpr std::uint64_t kWatchdogTimeoutNanoseconds = 500'000'000ULL;

enum class ApplyError {
    None,
    DecodeRejected,
    FirstSequenceNotZero,
    ReplayedSequence,
    RegressingTimestamp,
    RetiredSession,
};

struct HandSnapshot {
    HandState state{};
    bool has_state{};
    bool stale{true};
};

struct PublishedInputState {
    std::uint32_t buttons{};
    std::uint32_t touches{};
    float trigger{};
    float grip{};
    float stick_x{};
    float stick_y{};
};

bool IsPosePublishable(const HandSnapshot& snapshot) noexcept;
PublishedInputState InputForPublication(const HandSnapshot& snapshot) noexcept;

class StateStore {
public:
    ApplyError ApplyPacket(
        std::span<const std::uint8_t> packet,
        std::uint64_t arrival_nanoseconds) noexcept;

    HandSnapshot Snapshot(Hand hand, std::uint64_t now_nanoseconds) const noexcept;
    SessionId CurrentSession() const noexcept;

private:
    static std::size_t HandIndex(Hand hand) noexcept;
    static HandState NeutralState(Hand hand) noexcept;
    bool IsRetiredSession(const SessionId& session) const noexcept;
    void RetireCurrentSession() noexcept;
    void ResetForSession(const SessionId& session) noexcept;

    mutable std::mutex mutex_;
    SessionId session_{};
    bool has_session_{};
    std::uint64_t last_sequence_{};
    std::uint64_t last_heartbeat_timestamp_{};
    std::array<std::uint64_t, 2> last_hand_timestamps_{};
    std::uint64_t last_arrival_{};
    std::array<HandState, 2> states_{NeutralState(Hand::Left), NeutralState(Hand::Right)};
    std::array<bool, 2> has_state_{};
    std::array<SessionId, 16> retired_sessions_{};
    std::size_t retired_session_count_{};
    std::size_t next_retired_session_{};
};

}  // namespace ltb::driver
