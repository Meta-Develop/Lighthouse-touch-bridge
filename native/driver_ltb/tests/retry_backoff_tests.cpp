#include "ltb_driver/retry_backoff.hpp"

#include <cstdint>
#include <functional>
#include <iostream>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace {

using ltb::driver::RetryBackoff;

void Require(bool condition, const std::string& message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

void RequireDelays(
    RetryBackoff& backoff,
    const std::vector<std::uint32_t>& expected,
    const std::string& label) {
    for (std::size_t index = 0; index < expected.size(); ++index) {
        const std::uint32_t delay = backoff.NextDelayMilliseconds();
        Require(
            delay == expected[index],
            label + ": delay " + std::to_string(index) + " was " +
                std::to_string(delay) + ", expected " +
                std::to_string(expected[index]));
    }
}

void ScheduleDoublesFromInitialToCap() {
    RetryBackoff backoff(1'000, 30'000);
    RequireDelays(
        backoff,
        {1'000, 2'000, 4'000, 8'000, 16'000, 30'000, 30'000, 30'000},
        "doubling schedule");
}

void ResetRestoresInitialDelay() {
    RetryBackoff backoff(1'000, 30'000);
    RequireDelays(backoff, {1'000, 2'000, 4'000}, "pre-reset schedule");
    backoff.Reset();
    RequireDelays(backoff, {1'000, 2'000}, "post-reset schedule");
}

void ZeroInitialDelayClampsToOneMillisecond() {
    RetryBackoff backoff(0, 10);
    RequireDelays(backoff, {1, 2, 4, 8, 10, 10}, "zero-initial schedule");
}

void CapBelowInitialClampsToInitial() {
    RetryBackoff backoff(50, 10);
    RequireDelays(backoff, {50, 50, 50}, "cap-below-initial schedule");
}

void DoublingNearMaximumDoesNotOverflow() {
    RetryBackoff backoff(3'000'000'000U, 4'000'000'000U);
    RequireDelays(
        backoff,
        {3'000'000'000U, 4'000'000'000U, 4'000'000'000U},
        "near-maximum schedule");
}

}  // namespace

int main() {
    const std::vector<std::pair<std::string, std::function<void()>>> tests{
        {"doubling to cap", ScheduleDoublesFromInitialToCap},
        {"reset restores initial", ResetRestoresInitialDelay},
        {"zero initial clamps", ZeroInitialDelayClampsToOneMillisecond},
        {"cap below initial clamps", CapBelowInitialClampsToInitial},
        {"no overflow near maximum", DoublingNearMaximumDoesNotOverflow},
    };

    std::size_t failures = 0;
    for (const auto& [name, test] : tests) {
        try {
            test();
            std::cout << "PASS: " << name << '\n';
        } catch (const std::exception& error) {
            ++failures;
            std::cerr << "FAIL: " << name << ": " << error.what() << '\n';
        }
    }
    if (failures != 0) {
        std::cerr << failures << " test(s) failed\n";
        return 1;
    }
    std::cout << tests.size() << " tests passed\n";
    return 0;
}
