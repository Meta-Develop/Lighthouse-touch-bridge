#include "ltb_driver/state_store.hpp"

#include <cstddef>
#include <cstdint>

namespace ltb::driver {

namespace {

bool HasFlag(std::uint16_t flags, StateFlag flag) noexcept {
    return (flags & FlagValue(flag)) != 0U;
}

}  // namespace

bool IsPosePublishable(const HandSnapshot& snapshot) noexcept {
    const auto flags = snapshot.state.flags;
    return !snapshot.stale && snapshot.has_state &&
        HasFlag(flags, StateFlag::Connected) &&
        HasFlag(flags, StateFlag::Tracked) &&
        HasFlag(flags, StateFlag::OrientationValid) &&
        HasFlag(flags, StateFlag::PositionValid);
}

PublishedInputState InputForPublication(const HandSnapshot& snapshot) noexcept {
    const auto flags = snapshot.state.flags;
    if (snapshot.stale || !snapshot.has_state ||
        !HasFlag(flags, StateFlag::Connected) ||
        !HasFlag(flags, StateFlag::InputsValid)) {
        return {};
    }
    return {
        .buttons = snapshot.state.buttons,
        .touches = snapshot.state.touches,
        .trigger = snapshot.state.trigger,
        .grip = snapshot.state.grip,
        .stick_x = snapshot.state.stick_x,
        .stick_y = snapshot.state.stick_y,
    };
}

std::size_t StateStore::HandIndex(Hand hand) noexcept {
    return hand == Hand::Left ? 0U : 1U;
}

HandState StateStore::NeutralState(Hand hand) noexcept {
    HandState state{};
    state.hand = hand;
    state.orientation.w = 1.0F;
    return state;
}

bool StateStore::IsRetiredSession(const SessionId& session) const noexcept {
    for (std::size_t index = 0; index < retired_session_count_; ++index) {
        if (retired_sessions_[index] == session) {
            return true;
        }
    }
    return false;
}

void StateStore::RetireCurrentSession() noexcept {
    if (!has_session_) {
        return;
    }
    retired_sessions_[next_retired_session_] = session_;
    next_retired_session_ = (next_retired_session_ + 1U) % retired_sessions_.size();
    if (retired_session_count_ < retired_sessions_.size()) {
        ++retired_session_count_;
    }
}

void StateStore::ResetForSession(const SessionId& session) noexcept {
    session_ = session;
    has_session_ = true;
    last_sequence_ = 0;
    last_heartbeat_timestamp_ = 0;
    last_hand_timestamps_.fill(0);
    states_[0] = NeutralState(Hand::Left);
    states_[1] = NeutralState(Hand::Right);
    has_state_.fill(false);
}

ApplyError StateStore::ApplyPacket(
    std::span<const std::uint8_t> packet,
    std::uint64_t arrival_nanoseconds) noexcept {
    const auto decoded = DecodePacket(packet);
    if (!decoded) {
        return ApplyError::DecodeRejected;
    }

    std::scoped_lock lock(mutex_);
    const bool new_session = !has_session_ || decoded.message.session != session_;
    if (new_session && decoded.message.sequence != 0U) {
        return ApplyError::FirstSequenceNotZero;
    }
    if (new_session && IsRetiredSession(decoded.message.session)) {
        return ApplyError::RetiredSession;
    }
    if (!new_session && decoded.message.sequence <= last_sequence_) {
        return ApplyError::ReplayedSequence;
    }
    if (!new_session) {
        const auto prior_timestamp = decoded.message.type == MessageType::Heartbeat
            ? last_heartbeat_timestamp_
            : last_hand_timestamps_[HandIndex(decoded.message.state.hand)];
        if (decoded.message.monotonic_nanoseconds < prior_timestamp) {
            return ApplyError::RegressingTimestamp;
        }
    }

    if (new_session) {
        RetireCurrentSession();
        ResetForSession(decoded.message.session);
    }
    last_sequence_ = decoded.message.sequence;
    last_arrival_ = arrival_nanoseconds;
    if (decoded.message.type == MessageType::HandState) {
        const auto index = HandIndex(decoded.message.state.hand);
        last_hand_timestamps_[index] = decoded.message.monotonic_nanoseconds;
        states_[index] = decoded.message.state;
        states_[index].source_timestamp_nanoseconds = decoded.message.monotonic_nanoseconds;
        has_state_[index] = true;
    } else {
        last_heartbeat_timestamp_ = decoded.message.monotonic_nanoseconds;
    }
    return ApplyError::None;
}

HandSnapshot StateStore::Snapshot(Hand hand, std::uint64_t now_nanoseconds) const noexcept {
    std::scoped_lock lock(mutex_);
    const auto index = HandIndex(hand);
    const bool clock_regressed = now_nanoseconds < last_arrival_;
    const bool stale = !has_session_ || clock_regressed ||
        now_nanoseconds - last_arrival_ >= kWatchdogTimeoutNanoseconds;
    if (stale) {
        return {.state = NeutralState(hand), .has_state = has_state_[index], .stale = true};
    }

    HandState state = states_[index];
    if ((state.flags & FlagValue(StateFlag::InputsValid)) == 0U) {
        state.buttons = 0;
        state.touches = 0;
        state.trigger = 0.0F;
        state.grip = 0.0F;
        state.stick_x = 0.0F;
        state.stick_y = 0.0F;
    }
    return {.state = state, .has_state = has_state_[index], .stale = false};
}

SessionId StateStore::CurrentSession() const noexcept {
    std::scoped_lock lock(mutex_);
    return session_;
}

}  // namespace ltb::driver
