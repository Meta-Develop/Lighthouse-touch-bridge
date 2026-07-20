#pragma once

#include <cstdint>

namespace ltb::driver {

// Bounded exponential backoff schedule for retrying transient setup failures.
//
// NextDelayMilliseconds() yields the wait that precedes the next retry: the
// initial delay first, doubling after each consecutive failure, saturating at
// the configured maximum. Reset() restores the initial delay once an attempt
// succeeds. The schedule is platform-neutral; callers own the actual waiting
// and its interruption semantics.
class RetryBackoff final {
public:
    constexpr RetryBackoff(
        std::uint32_t initial_delay_ms, std::uint32_t maximum_delay_ms) noexcept
        : initial_delay_ms_(initial_delay_ms == 0U ? 1U : initial_delay_ms),
          maximum_delay_ms_(
              maximum_delay_ms < initial_delay_ms_ ? initial_delay_ms_
                                                   : maximum_delay_ms),
          next_delay_ms_(initial_delay_ms_) {}

    constexpr std::uint32_t NextDelayMilliseconds() noexcept {
        const std::uint32_t delay_ms = next_delay_ms_;
        next_delay_ms_ = next_delay_ms_ > maximum_delay_ms_ / 2U
            ? maximum_delay_ms_
            : next_delay_ms_ * 2U;
        return delay_ms;
    }

    constexpr void Reset() noexcept { next_delay_ms_ = initial_delay_ms_; }

private:
    std::uint32_t initial_delay_ms_;
    std::uint32_t maximum_delay_ms_;
    std::uint32_t next_delay_ms_;
};

}  // namespace ltb::driver
