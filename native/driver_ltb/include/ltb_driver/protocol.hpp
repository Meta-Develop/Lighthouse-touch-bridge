#pragma once

#include "ltb_driver/build_identity.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace ltb::driver {

inline constexpr std::size_t kHeaderSize = 16;
inline constexpr std::size_t kHeartbeatPacketSize = 48;
inline constexpr std::size_t kHandStatePacketSize = 132;
inline constexpr std::uint32_t kMagic = 0x3142544cU;

enum class MessageType : std::uint16_t {
    HandState = 1,
    Heartbeat = 2,
};

enum class Hand : std::uint8_t {
    Left = 1,
    Right = 2,
};

enum class StateFlag : std::uint16_t {
    Connected = 1U << 0U,
    OrientationValid = 1U << 1U,
    PositionValid = 1U << 2U,
    LinearVelocityValid = 1U << 3U,
    AngularVelocityValid = 1U << 4U,
    InputsValid = 1U << 5U,
    BatteryPresent = 1U << 6U,
    Tracked = 1U << 7U,
};

inline constexpr std::uint16_t kAllowedStateFlags = 0x00bfU;
inline constexpr bool kProtocolV1ProvidesBatteryStatus = false;
inline constexpr std::uint32_t kAllowedButtonBits = 0x0000001fU;
inline constexpr std::uint32_t kAllowedTouchBits = 0x0000007fU;

enum class ButtonBit : std::uint32_t {
    Primary = 1U << 0U,
    Secondary = 1U << 1U,
    Menu = 1U << 2U,
    StickClick = 1U << 3U,
    TriggerClick = 1U << 4U,
};

enum class TouchBit : std::uint32_t {
    Primary = 1U << 0U,
    Secondary = 1U << 1U,
    Trigger = 1U << 2U,
    Stick = 1U << 3U,
    Thumbrest = 1U << 4U,
    IndexPointing = 1U << 5U,
    ThumbUp = 1U << 6U,
};

constexpr std::uint16_t FlagValue(StateFlag flag) noexcept {
    return static_cast<std::uint16_t>(flag);
}

constexpr std::uint32_t ButtonValue(ButtonBit bit) noexcept {
    return static_cast<std::uint32_t>(bit);
}

constexpr std::uint32_t TouchValue(TouchBit bit) noexcept {
    return static_cast<std::uint32_t>(bit);
}

struct SessionId {
    std::array<std::uint8_t, 16> bytes{};

    bool operator==(const SessionId&) const = default;
};

struct Vector3 {
    float x{};
    float y{};
    float z{};
};

struct Quaternion {
    float x{};
    float y{};
    float z{};
    float w{1.0F};
};

struct HandState {
    Hand hand{Hand::Left};
    std::uint16_t flags{};
    // This is populated from the message ordering field by StateStore. It is
    // not an additional wire field. The value is producer QPC time in ns.
    std::uint64_t source_timestamp_nanoseconds{};
    Vector3 position{};
    Quaternion orientation{};
    Vector3 linear_velocity{};
    Vector3 angular_velocity{};
    std::uint32_t buttons{};
    std::uint32_t touches{};
    float trigger{};
    float grip{};
    float stick_x{};
    float stick_y{};
    float battery{};
};

struct Message {
    MessageType type{MessageType::Heartbeat};
    SessionId session{};
    std::uint64_t sequence{};
    std::uint64_t monotonic_nanoseconds{};
    HandState state{};
};

enum class DecodeError {
    None,
    Truncated,
    BadMagic,
    BadVersion,
    BadType,
    BadLength,
    ReservedNonZero,
    ZeroSession,
    InvalidTimestamp,
    InvalidHand,
    InvalidFlags,
    InvalidButtons,
    InvalidTouches,
    NonFinite,
    InvalidQuaternion,
    InvalidAnalog,
    InvalidBattery,
};

struct DecodeResult {
    Message message{};
    DecodeError error{DecodeError::None};

    explicit operator bool() const noexcept {
        return error == DecodeError::None;
    }
};

DecodeResult DecodePacket(std::span<const std::uint8_t> packet) noexcept;

}  // namespace ltb::driver
