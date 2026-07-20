#include "ltb_driver/protocol.hpp"

#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <type_traits>

namespace ltb::driver {
namespace {

constexpr std::size_t kOffsetMagic = 0;
constexpr std::size_t kOffsetMajor = 4;
constexpr std::size_t kOffsetMinor = 6;
constexpr std::size_t kOffsetType = 8;
constexpr std::size_t kOffsetHeaderReserved = 10;
constexpr std::size_t kOffsetPayloadLength = 12;
constexpr std::size_t kOffsetSession = 16;
constexpr std::size_t kOffsetSequence = 32;
constexpr std::size_t kOffsetTimestamp = 40;
constexpr std::size_t kOffsetHand = 48;
constexpr std::size_t kOffsetIdentityReserved = 49;
constexpr std::size_t kOffsetFlags = 50;
constexpr std::size_t kOffsetPosition = 52;
constexpr std::size_t kOffsetOrientation = 64;
constexpr std::size_t kOffsetLinearVelocity = 80;
constexpr std::size_t kOffsetAngularVelocity = 92;
constexpr std::size_t kOffsetButtons = 104;
constexpr std::size_t kOffsetTouches = 108;
constexpr std::size_t kOffsetTrigger = 112;
constexpr std::size_t kOffsetGrip = 116;
constexpr std::size_t kOffsetStickX = 120;
constexpr std::size_t kOffsetStickY = 124;
constexpr std::size_t kOffsetBattery = 128;

template <typename T>
T ReadLittleEndian(std::span<const std::uint8_t> packet, std::size_t offset) noexcept {
    static_assert(std::is_integral_v<T>);
    using Unsigned = std::make_unsigned_t<T>;
    Unsigned value{};
    for (std::size_t index = 0; index < sizeof(T); ++index) {
        value |= static_cast<Unsigned>(packet[offset + index]) << (index * 8U);
    }
    return static_cast<T>(value);
}

float ReadFloat(std::span<const std::uint8_t> packet, std::size_t offset) noexcept {
    return std::bit_cast<float>(ReadLittleEndian<std::uint32_t>(packet, offset));
}

Vector3 ReadVector3(std::span<const std::uint8_t> packet, std::size_t offset) noexcept {
    return {ReadFloat(packet, offset), ReadFloat(packet, offset + 4), ReadFloat(packet, offset + 8)};
}

bool IsFinite(float value) noexcept {
    return std::isfinite(value);
}

bool IsFinite(const Vector3& value) noexcept {
    return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
}

bool IsFinite(const Quaternion& value) noexcept {
    return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
}

bool InRange(float value, float minimum, float maximum) noexcept {
    return value >= minimum && value <= maximum;
}

bool IsZeroSession(const SessionId& session) noexcept {
    for (const auto value : session.bytes) {
        if (value != 0U) {
            return false;
        }
    }
    return true;
}

DecodeResult Failure(DecodeError error) noexcept {
    return {.message = {}, .error = error};
}

}  // namespace

DecodeResult DecodePacket(std::span<const std::uint8_t> packet) noexcept {
    if (packet.size() < kHeaderSize) {
        return Failure(DecodeError::Truncated);
    }
    if (ReadLittleEndian<std::uint32_t>(packet, kOffsetMagic) != kMagic) {
        return Failure(DecodeError::BadMagic);
    }
    if (ReadLittleEndian<std::uint16_t>(packet, kOffsetMajor) != kProtocolMajor ||
        ReadLittleEndian<std::uint16_t>(packet, kOffsetMinor) != kProtocolMinor) {
        return Failure(DecodeError::BadVersion);
    }
    if (ReadLittleEndian<std::uint16_t>(packet, kOffsetHeaderReserved) != 0U) {
        return Failure(DecodeError::ReservedNonZero);
    }

    const auto raw_type = ReadLittleEndian<std::uint16_t>(packet, kOffsetType);
    MessageType type{};
    std::size_t expected_size{};
    if (raw_type == static_cast<std::uint16_t>(MessageType::HandState)) {
        type = MessageType::HandState;
        expected_size = kHandStatePacketSize;
    } else if (raw_type == static_cast<std::uint16_t>(MessageType::Heartbeat)) {
        type = MessageType::Heartbeat;
        expected_size = kHeartbeatPacketSize;
    } else {
        return Failure(DecodeError::BadType);
    }

    const auto payload_length = ReadLittleEndian<std::uint32_t>(packet, kOffsetPayloadLength);
    if (packet.size() != expected_size || payload_length != expected_size - kHeaderSize) {
        return packet.size() < expected_size
            ? Failure(DecodeError::Truncated)
            : Failure(DecodeError::BadLength);
    }

    Message message{};
    message.type = type;
    for (std::size_t index = 0; index < message.session.bytes.size(); ++index) {
        message.session.bytes[index] = packet[kOffsetSession + index];
    }
    if (IsZeroSession(message.session)) {
        return Failure(DecodeError::ZeroSession);
    }
    message.sequence = ReadLittleEndian<std::uint64_t>(packet, kOffsetSequence);
    message.monotonic_nanoseconds = ReadLittleEndian<std::uint64_t>(packet, kOffsetTimestamp);
    if (message.monotonic_nanoseconds == 0U) {
        return Failure(DecodeError::InvalidTimestamp);
    }

    if (type == MessageType::Heartbeat) {
        return {.message = message, .error = DecodeError::None};
    }

    if (packet[kOffsetIdentityReserved] != 0U) {
        return Failure(DecodeError::ReservedNonZero);
    }
    const auto raw_hand = packet[kOffsetHand];
    if (raw_hand != static_cast<std::uint8_t>(Hand::Left) &&
        raw_hand != static_cast<std::uint8_t>(Hand::Right)) {
        return Failure(DecodeError::InvalidHand);
    }
    message.state.hand = static_cast<Hand>(raw_hand);
    message.state.flags = ReadLittleEndian<std::uint16_t>(packet, kOffsetFlags);
    if ((message.state.flags & ~kAllowedStateFlags) != 0U) {
        return Failure(DecodeError::InvalidFlags);
    }
    const bool connected = (message.state.flags & FlagValue(StateFlag::Connected)) != 0U;
    const bool tracked = (message.state.flags & FlagValue(StateFlag::Tracked)) != 0U;
    const bool orientation_valid =
        (message.state.flags & FlagValue(StateFlag::OrientationValid)) != 0U;
    const bool position_valid =
        (message.state.flags & FlagValue(StateFlag::PositionValid)) != 0U;
    constexpr std::uint16_t kConnectedDependentFlags =
        FlagValue(StateFlag::OrientationValid) |
        FlagValue(StateFlag::PositionValid) |
        FlagValue(StateFlag::LinearVelocityValid) |
        FlagValue(StateFlag::AngularVelocityValid) |
        FlagValue(StateFlag::InputsValid) |
        FlagValue(StateFlag::Tracked);
    if ((tracked && (!connected || !orientation_valid || !position_valid)) ||
        (!connected && (message.state.flags & kConnectedDependentFlags) != 0U)) {
        return Failure(DecodeError::InvalidFlags);
    }

    message.state.position = ReadVector3(packet, kOffsetPosition);
    message.state.orientation = {
        ReadFloat(packet, kOffsetOrientation),
        ReadFloat(packet, kOffsetOrientation + 4),
        ReadFloat(packet, kOffsetOrientation + 8),
        ReadFloat(packet, kOffsetOrientation + 12),
    };
    message.state.linear_velocity = ReadVector3(packet, kOffsetLinearVelocity);
    message.state.angular_velocity = ReadVector3(packet, kOffsetAngularVelocity);
    message.state.buttons = ReadLittleEndian<std::uint32_t>(packet, kOffsetButtons);
    message.state.touches = ReadLittleEndian<std::uint32_t>(packet, kOffsetTouches);
    message.state.trigger = ReadFloat(packet, kOffsetTrigger);
    message.state.grip = ReadFloat(packet, kOffsetGrip);
    message.state.stick_x = ReadFloat(packet, kOffsetStickX);
    message.state.stick_y = ReadFloat(packet, kOffsetStickY);
    message.state.battery = ReadFloat(packet, kOffsetBattery);

    if (!IsFinite(message.state.position) || !IsFinite(message.state.orientation) ||
        !IsFinite(message.state.linear_velocity) || !IsFinite(message.state.angular_velocity) ||
        !IsFinite(message.state.trigger) || !IsFinite(message.state.grip) ||
        !IsFinite(message.state.stick_x) || !IsFinite(message.state.stick_y) ||
        !IsFinite(message.state.battery)) {
        return Failure(DecodeError::NonFinite);
    }
    const bool linear_velocity_valid =
        (message.state.flags & FlagValue(StateFlag::LinearVelocityValid)) != 0U;
    if (!linear_velocity_valid &&
        (message.state.linear_velocity.x != 0.0F ||
         message.state.linear_velocity.y != 0.0F ||
         message.state.linear_velocity.z != 0.0F)) {
        return Failure(DecodeError::InvalidFlags);
    }
    const bool angular_velocity_valid =
        (message.state.flags & FlagValue(StateFlag::AngularVelocityValid)) != 0U;
    if (!angular_velocity_valid &&
        (message.state.angular_velocity.x != 0.0F ||
         message.state.angular_velocity.y != 0.0F ||
         message.state.angular_velocity.z != 0.0F)) {
        return Failure(DecodeError::InvalidFlags);
    }
    const auto quaternion_norm = std::sqrt(
        message.state.orientation.x * message.state.orientation.x +
        message.state.orientation.y * message.state.orientation.y +
        message.state.orientation.z * message.state.orientation.z +
        message.state.orientation.w * message.state.orientation.w);
    if (std::abs(quaternion_norm - 1.0F) > 0.001F) {
        return Failure(DecodeError::InvalidQuaternion);
    }
    if ((message.state.buttons & ~kAllowedButtonBits) != 0U) {
        return Failure(DecodeError::InvalidButtons);
    }
    if ((message.state.touches & ~kAllowedTouchBits) != 0U) {
        return Failure(DecodeError::InvalidTouches);
    }
    if (!InRange(message.state.trigger, 0.0F, 1.0F) ||
        !InRange(message.state.grip, 0.0F, 1.0F) ||
        !InRange(message.state.stick_x, -1.0F, 1.0F) ||
        !InRange(message.state.stick_y, -1.0F, 1.0F)) {
        return Failure(DecodeError::InvalidAnalog);
    }
    const bool inputs_valid =
        (message.state.flags & FlagValue(StateFlag::InputsValid)) != 0U;
    if (!inputs_valid &&
        (message.state.buttons != 0U || message.state.touches != 0U ||
         message.state.trigger != 0.0F || message.state.grip != 0.0F ||
         message.state.stick_x != 0.0F || message.state.stick_y != 0.0F)) {
        return Failure(DecodeError::InvalidAnalog);
    }
    const bool battery_present =
        (message.state.flags & FlagValue(StateFlag::BatteryPresent)) != 0U;
    if ((battery_present && !InRange(message.state.battery, 0.0F, 1.0F)) ||
        (!battery_present && message.state.battery != 0.0F)) {
        return Failure(DecodeError::InvalidBattery);
    }

    return {.message = message, .error = DecodeError::None};
}

}  // namespace ltb::driver
