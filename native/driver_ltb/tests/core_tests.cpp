#include "ltb_driver/protocol.hpp"
#include "ltb_driver/state_store.hpp"

#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace {

using ltb::driver::ApplyError;
using ltb::driver::ButtonBit;
using ltb::driver::ButtonValue;
using ltb::driver::DecodeError;
using ltb::driver::Hand;
using ltb::driver::MessageType;
using ltb::driver::StateFlag;
using ltb::driver::StateStore;
using ltb::driver::TouchBit;
using ltb::driver::TouchValue;

void Require(bool condition, const std::string& message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

template <typename T>
void WriteInteger(std::vector<std::uint8_t>& packet, std::size_t offset, T value) {
    using Unsigned = std::make_unsigned_t<T>;
    const auto unsigned_value = static_cast<Unsigned>(value);
    for (std::size_t index = 0; index < sizeof(T); ++index) {
        packet.at(offset + index) = static_cast<std::uint8_t>(
            (unsigned_value >> (index * 8U)) & static_cast<Unsigned>(0xffU));
    }
}

void WriteFloat(std::vector<std::uint8_t>& packet, std::size_t offset, float value) {
    WriteInteger(packet, offset, std::bit_cast<std::uint32_t>(value));
}

std::vector<std::uint8_t> HandPacket(
    std::uint8_t session_seed = 1,
    std::uint64_t sequence = 0,
    std::uint64_t timestamp = 1'000'000'000ULL,
    Hand hand = Hand::Left) {
    std::vector<std::uint8_t> packet(ltb::driver::kHandStatePacketSize, 0U);
    WriteInteger(packet, 0, ltb::driver::kMagic);
    WriteInteger(packet, 4, ltb::driver::kProtocolMajor);
    WriteInteger(packet, 6, ltb::driver::kProtocolMinor);
    WriteInteger(packet, 8, static_cast<std::uint16_t>(MessageType::HandState));
    WriteInteger(packet, 12, static_cast<std::uint32_t>(116));
    for (std::size_t index = 0; index < 16; ++index) {
        packet[16 + index] = static_cast<std::uint8_t>(session_seed + index);
    }
    WriteInteger(packet, 32, sequence);
    WriteInteger(packet, 40, timestamp);
    packet[48] = static_cast<std::uint8_t>(hand);
    WriteInteger(packet, 50, ltb::driver::kAllowedStateFlags);
    WriteFloat(packet, 52, 1.0F);
    WriteFloat(packet, 56, 2.0F);
    WriteFloat(packet, 60, 3.0F);
    WriteFloat(packet, 64, 0.0F);
    WriteFloat(packet, 68, 0.0F);
    WriteFloat(packet, 72, 0.0F);
    WriteFloat(packet, 76, 1.0F);
    WriteFloat(packet, 80, 0.25F);
    WriteFloat(packet, 84, -0.5F);
    WriteFloat(packet, 88, 0.75F);
    WriteFloat(packet, 92, 1.0F);
    WriteFloat(packet, 96, -1.0F);
    WriteFloat(packet, 100, 0.5F);
    WriteInteger(
        packet,
        104,
        ButtonValue(ButtonBit::Primary) | ButtonValue(ButtonBit::Menu) |
            ButtonValue(ButtonBit::TriggerClick));
    WriteInteger(
        packet,
        108,
        TouchValue(TouchBit::Primary) | TouchValue(TouchBit::Secondary) |
            TouchValue(TouchBit::Stick));
    WriteFloat(packet, 112, 0.25F);
    WriteFloat(packet, 116, 0.75F);
    WriteFloat(packet, 120, -0.5F);
    WriteFloat(packet, 124, 0.5F);
    WriteFloat(packet, 128, 0.5F);
    return packet;
}

std::vector<std::uint8_t> HeartbeatPacket(
    std::uint8_t session_seed,
    std::uint64_t sequence,
    std::uint64_t timestamp) {
    std::vector<std::uint8_t> packet(ltb::driver::kHeartbeatPacketSize, 0U);
    WriteInteger(packet, 0, ltb::driver::kMagic);
    WriteInteger(packet, 4, ltb::driver::kProtocolMajor);
    WriteInteger(packet, 6, ltb::driver::kProtocolMinor);
    WriteInteger(packet, 8, static_cast<std::uint16_t>(MessageType::Heartbeat));
    WriteInteger(packet, 12, static_cast<std::uint32_t>(32));
    for (std::size_t index = 0; index < 16; ++index) {
        packet[16 + index] = static_cast<std::uint8_t>(session_seed + index);
    }
    WriteInteger(packet, 32, sequence);
    WriteInteger(packet, 40, timestamp);
    return packet;
}

void NeutralizeInputs(std::vector<std::uint8_t>& packet) {
    WriteInteger(packet, 104, static_cast<std::uint32_t>(0));
    WriteInteger(packet, 108, static_cast<std::uint32_t>(0));
    WriteFloat(packet, 112, 0.0F);
    WriteFloat(packet, 116, 0.0F);
    WriteFloat(packet, 120, 0.0F);
    WriteFloat(packet, 124, 0.0F);
}

void NeutralizeVelocities(std::vector<std::uint8_t>& packet) {
    for (const std::size_t offset : {80U, 84U, 88U, 92U, 96U, 100U}) {
        WriteFloat(packet, offset, 0.0F);
    }
}

std::vector<std::uint8_t> ParseHex(const std::string& hex) {
    Require(hex.size() % 2U == 0U, "hex fixture has odd length");
    const auto digit = [](char value) -> std::uint8_t {
        if (value >= '0' && value <= '9') {
            return static_cast<std::uint8_t>(value - '0');
        }
        if (value >= 'A' && value <= 'F') {
            return static_cast<std::uint8_t>(value - 'A' + 10);
        }
        throw std::runtime_error("hex fixture contains an invalid digit");
    };
    std::vector<std::uint8_t> bytes(hex.size() / 2U);
    for (std::size_t index = 0; index < bytes.size(); ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            (digit(hex[index * 2U]) << 4U) | digit(hex[index * 2U + 1U]));
    }
    return bytes;
}

void GoldenPacketDecodes() {
    const auto hand_bytes = ParseHex(
        "4C544231010000000100000074000000"
        "0102030405060708090A0B0C0D0E0F10"
        "1112131415161718191A1B1C1D1E1F20"
        "0100FF00"
        "0000803F000000C00000003F"
        "0000000000000000000000000000803F"
        "0000803E000000BF0000C03F"
        "00000040000040C000008040"
        "0500000012000000"
        "0000803E0000403F000000BF0000003FCDCC4C3F");
    Require(hand_bytes.size() == ltb::driver::kHandStatePacketSize, "golden hand byte count changed");
    const auto result = ltb::driver::DecodePacket(hand_bytes);
    Require(static_cast<bool>(result), "golden hand packet was rejected");
    Require(result.message.type == MessageType::HandState, "wrong message type");
    Require(result.message.sequence == 0x1817161514131211ULL, "wrong sequence");
    Require(result.message.monotonic_nanoseconds == 0x201f1e1d1c1b1a19ULL, "wrong timestamp");
    Require(result.message.state.hand == Hand::Left, "wrong hand");
    Require(result.message.state.position.x == 1.0F, "wrong position x");
    Require(result.message.state.position.y == -2.0F, "wrong position y");
    Require(result.message.state.position.z == 0.5F, "wrong position z");
    Require(result.message.state.orientation.w == 1.0F, "wrong quaternion order");
    Require(result.message.state.flags == 0x00ffU, "wrong state flags");
    Require(
        result.message.state.touches ==
            (TouchValue(TouchBit::Secondary) | TouchValue(TouchBit::Thumbrest)),
        "wrong golden touch bits");
    Require(result.message.state.stick_x == -0.5F, "wrong stick x");
    Require(std::abs(result.message.state.battery - 0.8F) < 0.0001F, "wrong battery");

    const auto heartbeat_bytes = ParseHex(
        "4C544231010000000200000020000000"
        "0102030405060708090A0B0C0D0E0F10"
        "1112131415161718191A1B1C1D1E1F20");
    Require(
        heartbeat_bytes.size() == ltb::driver::kHeartbeatPacketSize,
        "golden heartbeat byte count changed");
    const auto heartbeat = ltb::driver::DecodePacket(heartbeat_bytes);
    Require(static_cast<bool>(heartbeat), "golden heartbeat was rejected");
    Require(heartbeat.message.type == MessageType::Heartbeat, "wrong heartbeat type");
    Require(heartbeat.message.sequence == result.message.sequence, "golden ordering differs");
    Require(
        heartbeat.message.monotonic_nanoseconds == result.message.monotonic_nanoseconds,
        "golden timestamp differs");
}

void HeaderFailuresAreRejected() {
    auto packet = HandPacket();
    packet.resize(15);
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::Truncated, "short header accepted");

    packet = HandPacket();
    packet[0] ^= 0xffU;
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::BadMagic, "bad magic accepted");

    packet = HandPacket();
    WriteInteger(packet, 4, static_cast<std::uint16_t>(2));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::BadVersion, "bad version accepted");

    packet = HandPacket();
    WriteInteger(packet, 8, static_cast<std::uint16_t>(99));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::BadType, "bad type accepted");

    packet = HandPacket();
    WriteInteger(packet, 10, static_cast<std::uint16_t>(1));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::ReservedNonZero, "reserved header accepted");

    packet = HeartbeatPacket(1, 0, 0);
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidTimestamp,
        "zero timestamp accepted");
}

void LengthFailuresAreRejected() {
    auto packet = HandPacket();
    packet.pop_back();
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::Truncated, "truncated payload accepted");

    packet = HandPacket();
    packet.push_back(0);
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::BadLength, "trailing byte accepted");

    packet = HandPacket();
    WriteInteger(packet, 12, static_cast<std::uint32_t>(115));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::BadLength, "bad payload length accepted");
}

void IdentityAndRangeFailuresAreRejected() {
    auto packet = HandPacket();
    for (std::size_t index = 16; index < 32; ++index) {
        packet[index] = 0;
    }
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::ZeroSession, "zero session accepted");

    packet = HandPacket();
    packet[48] = 3;
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::InvalidHand, "bad hand accepted");

    packet = HandPacket();
    packet[49] = 1;
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::ReservedNonZero, "reserved identity accepted");

    packet = HandPacket();
    WriteInteger(packet, 50, static_cast<std::uint16_t>(0x100));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::InvalidFlags, "unknown flag accepted");

    packet = HandPacket();
    WriteInteger(packet, 104, static_cast<std::uint32_t>(0x20));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::InvalidButtons, "unknown button accepted");

    packet = HandPacket();
    WriteInteger(packet, 108, static_cast<std::uint32_t>(0x80));
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::InvalidTouches, "unknown touch accepted");

    packet = HandPacket();
    WriteInteger(
        packet,
        108,
        TouchValue(TouchBit::IndexPointing) | TouchValue(TouchBit::ThumbUp));
    Require(static_cast<bool>(ltb::driver::DecodePacket(packet)), "gesture touch bits rejected");

    packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(
            ltb::driver::kAllowedStateFlags & ~ltb::driver::FlagValue(StateFlag::PositionValid)));
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidFlags,
        "tracked state without valid position accepted");

    packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(
            ltb::driver::FlagValue(StateFlag::BatteryPresent) |
            ltb::driver::FlagValue(StateFlag::OrientationValid)));
    NeutralizeInputs(packet);
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidFlags,
        "disconnected state with tracking flags accepted");

    packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(ltb::driver::FlagValue(StateFlag::BatteryPresent)));
    NeutralizeVelocities(packet);
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidAnalog,
        "disconnected state with non-neutral inputs accepted");

    NeutralizeInputs(packet);
    Require(
        static_cast<bool>(ltb::driver::DecodePacket(packet)),
        "battery telemetry was incorrectly tied to connection state");
}

void NonFiniteAndQuaternionFailuresAreRejected() {
    auto packet = HandPacket();
    WriteFloat(packet, 52, std::numeric_limits<float>::quiet_NaN());
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::NonFinite, "NaN accepted");

    packet = HandPacket();
    WriteFloat(packet, 76, std::numeric_limits<float>::infinity());
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::NonFinite, "infinity accepted");

    packet = HandPacket();
    WriteFloat(packet, 76, 0.0F);
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidQuaternion,
        "degenerate quaternion accepted");

    packet = HandPacket();
    WriteFloat(packet, 76, 1.01F);
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidQuaternion,
        "non-unit quaternion accepted");
}

void AnalogAndBatteryFailuresAreRejected() {
    auto packet = HandPacket();
    WriteFloat(packet, 112, 1.01F);
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::InvalidAnalog, "trigger > 1 accepted");

    packet = HandPacket();
    WriteFloat(packet, 120, -1.01F);
    Require(ltb::driver::DecodePacket(packet).error == DecodeError::InvalidAnalog, "stick < -1 accepted");

    packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(ltb::driver::kAllowedStateFlags &
            ~ltb::driver::FlagValue(StateFlag::BatteryPresent)));
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidBattery,
        "nonzero absent battery accepted");

    packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(
            ltb::driver::kAllowedStateFlags &
            ~ltb::driver::FlagValue(StateFlag::LinearVelocityValid)));
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidFlags,
        "nonzero invalid linear velocity accepted");
    WriteFloat(packet, 80, 0.0F);
    WriteFloat(packet, 84, 0.0F);
    WriteFloat(packet, 88, 0.0F);
    Require(
        static_cast<bool>(ltb::driver::DecodePacket(packet)),
        "zero invalid linear velocity was rejected");

    packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(
            ltb::driver::kAllowedStateFlags &
            ~ltb::driver::FlagValue(StateFlag::AngularVelocityValid)));
    Require(
        ltb::driver::DecodePacket(packet).error == DecodeError::InvalidFlags,
        "nonzero invalid angular velocity accepted");
    WriteFloat(packet, 92, 0.0F);
    WriteFloat(packet, 96, 0.0F);
    WriteFloat(packet, 100, 0.0F);
    Require(
        static_cast<bool>(ltb::driver::DecodePacket(packet)),
        "zero invalid angular velocity was rejected");
}

void ReplayAndTimestampRegressionAreRejected() {
    StateStore store;
    auto packet = HeartbeatPacket(1, 0, 200);
    Require(store.ApplyPacket(packet, 1'000) == ApplyError::None, "initial heartbeat rejected");
    Require(store.ApplyPacket(packet, 1'001) == ApplyError::ReplayedSequence, "replay accepted");

    Require(
        store.ApplyPacket(HandPacket(1, 1, 150, Hand::Left), 1'002) == ApplyError::None,
        "left sample older than heartbeat was rejected");
    Require(
        store.ApplyPacket(HandPacket(1, 2, 140, Hand::Right), 1'003) == ApplyError::None,
        "right sample older than heartbeat and left sample was rejected");
    Require(
        store.ApplyPacket(HandPacket(1, 3, 149, Hand::Left), 1'004) ==
            ApplyError::RegressingTimestamp,
        "same-hand regressing timestamp accepted");

    packet = HeartbeatPacket(1, 3, 199);
    Require(
        store.ApplyPacket(packet, 1'005) == ApplyError::RegressingTimestamp,
        "same-kind heartbeat regression accepted");

    packet = HandPacket(1, 3, 150, Hand::Left);
    Require(store.ApplyPacket(packet, 1'006) == ApplyError::None, "equal same-hand timestamp rejected");
}

void NewSessionRequiresZeroAndResetsState() {
    StateStore store;
    Require(store.ApplyPacket(HandPacket(1, 0, 100), 1'000) == ApplyError::None, "first session failed");
    Require(
        store.ApplyPacket(HandPacket(20, 3, 101), 2'000) == ApplyError::FirstSequenceNotZero,
        "new session nonzero sequence accepted");
    Require(
        store.ApplyPacket(HandPacket(20, 0, 50, Hand::Right), 2'001) == ApplyError::None,
        "new session with an older timestamp was rejected");
    const auto left = store.Snapshot(Hand::Left, 2'002);
    const auto right = store.Snapshot(Hand::Right, 2'002);
    Require(!left.has_state, "old-session left state survived reset");
    Require(right.has_state, "new-session right state missing");

    Require(
        store.ApplyPacket(HandPacket(1, 0, 25), 2'003) == ApplyError::RetiredSession,
        "retired session was accepted");
}

void PerHandStateIsIsolated() {
    StateStore store;
    Require(store.ApplyPacket(HandPacket(1, 0, 100, Hand::Left), 1'000) == ApplyError::None, "left failed");
    auto right_packet = HandPacket(1, 1, 101, Hand::Right);
    WriteFloat(right_packet, 52, 9.0F);
    Require(store.ApplyPacket(right_packet, 1'100) == ApplyError::None, "right failed");
    Require(store.Snapshot(Hand::Left, 1'101).state.position.x == 1.0F, "right changed left state");
    Require(store.Snapshot(Hand::Right, 1'101).state.position.x == 9.0F, "right state missing");

    right_packet[48] = 9;
    Require(store.ApplyPacket(right_packet, 1'200) == ApplyError::DecodeRejected, "invalid right accepted");
    Require(store.Snapshot(Hand::Left, 1'201).state.position.x == 1.0F, "invalid right changed left");
}

void WatchdogNeutralizesBothHandsAtFiveHundredMilliseconds() {
    StateStore store;
    Require(store.ApplyPacket(HandPacket(1, 0, 100, Hand::Left), 1'000) == ApplyError::None, "left failed");
    Require(store.ApplyPacket(HandPacket(1, 1, 101, Hand::Right), 1'100) == ApplyError::None, "right failed");
    Require(!store.Snapshot(Hand::Left, 500'001'099).stale, "state expired before 500 ms");

    const auto left = store.Snapshot(Hand::Left, 500'001'100);
    const auto right = store.Snapshot(Hand::Right, 500'001'100);
    Require(left.stale && right.stale, "500 ms watchdog did not expire both hands");
    Require(
        !ltb::driver::IsPosePublishable(left) && !ltb::driver::IsPosePublishable(right),
        "stale watchdog snapshots remained pose-publishable");
    Require(left.state.flags == 0 && right.state.flags == 0, "stale flags were not neutral");
    Require(left.state.buttons == 0 && right.state.touches == 0, "stale digital input was not neutral");
    Require(left.state.trigger == 0.0F && right.state.stick_x == 0.0F, "stale analog input was not neutral");
}

void HeartbeatRefreshesWatchdogWithoutChangingHandState() {
    StateStore store;
    Require(store.ApplyPacket(HandPacket(1, 0, 100), 1'000) == ApplyError::None, "state failed");
    Require(store.ApplyPacket(HeartbeatPacket(1, 1, 200), 400'000'000) == ApplyError::None, "heartbeat failed");
    const auto snapshot = store.Snapshot(Hand::Left, 800'000'000);
    Require(!snapshot.stale, "heartbeat did not refresh watchdog");
    Require(snapshot.state.position.x == 1.0F, "heartbeat changed state");
}

void InvalidInputsArePublishedNeutral() {
    StateStore store;
    auto packet = HandPacket();
    WriteInteger(
        packet,
        50,
        static_cast<std::uint16_t>(ltb::driver::kAllowedStateFlags &
            ~ltb::driver::FlagValue(StateFlag::InputsValid)));
    NeutralizeInputs(packet);
    Require(store.ApplyPacket(packet, 1'000) == ApplyError::None, "input-invalid state rejected");
    const auto snapshot = store.Snapshot(Hand::Left, 1'001);
    Require(snapshot.state.buttons == 0 && snapshot.state.touches == 0, "digital input not neutral");
    Require(snapshot.state.trigger == 0.0F && snapshot.state.grip == 0.0F, "analog input not neutral");
}

void PublicationBoundaryCannotLatchInputs() {
    ltb::driver::HandSnapshot snapshot{};
    snapshot.has_state = true;
    snapshot.stale = false;
    snapshot.state.hand = Hand::Left;
    snapshot.state.flags = ltb::driver::FlagValue(StateFlag::Connected) |
        ltb::driver::FlagValue(StateFlag::OrientationValid) |
        ltb::driver::FlagValue(StateFlag::PositionValid) |
        ltb::driver::FlagValue(StateFlag::InputsValid) |
        ltb::driver::FlagValue(StateFlag::Tracked);
    snapshot.state.buttons = ButtonValue(ButtonBit::Primary);
    snapshot.state.touches = TouchValue(TouchBit::Trigger);
    snapshot.state.trigger = 0.75F;
    snapshot.state.grip = 0.5F;
    snapshot.state.stick_x = -0.25F;
    snapshot.state.stick_y = 0.25F;

    const auto live = ltb::driver::InputForPublication(snapshot);
    Require(live.buttons != 0 && live.touches != 0, "valid live digital input was neutralized");
    Require(live.trigger == 0.75F && live.stick_x == -0.25F, "valid live analog input was neutralized");
    Require(ltb::driver::IsPosePublishable(snapshot), "valid tracked snapshot was not pose-publishable");

    const auto require_neutral = [](const ltb::driver::PublishedInputState& input, const std::string& context) {
        Require(input.buttons == 0 && input.touches == 0, context + " retained digital input");
        Require(
            input.trigger == 0.0F && input.grip == 0.0F &&
                input.stick_x == 0.0F && input.stick_y == 0.0F,
            context + " retained analog input");
    };

    snapshot.stale = true;
    require_neutral(ltb::driver::InputForPublication(snapshot), "stale snapshot");
    Require(!ltb::driver::IsPosePublishable(snapshot), "stale snapshot remained pose-publishable");

    snapshot.stale = false;
    snapshot.state.flags = static_cast<std::uint16_t>(
        snapshot.state.flags & ~ltb::driver::FlagValue(StateFlag::Connected));
    require_neutral(ltb::driver::InputForPublication(snapshot), "disconnected snapshot");

    snapshot.state.flags = ltb::driver::FlagValue(StateFlag::Connected) |
        ltb::driver::FlagValue(StateFlag::OrientationValid) |
        ltb::driver::FlagValue(StateFlag::PositionValid) |
        ltb::driver::FlagValue(StateFlag::Tracked);
    require_neutral(ltb::driver::InputForPublication(snapshot), "inputs-invalid snapshot");

    snapshot.state.flags |= ltb::driver::FlagValue(StateFlag::InputsValid);
    snapshot.has_state = false;
    require_neutral(ltb::driver::InputForPublication(snapshot), "missing-state snapshot");
}

}  // namespace

int main() {
    const std::vector<std::pair<std::string, std::function<void()>>> tests{
        {"golden packet decode", GoldenPacketDecodes},
        {"header rejection", HeaderFailuresAreRejected},
        {"length rejection", LengthFailuresAreRejected},
        {"identity and range rejection", IdentityAndRangeFailuresAreRejected},
        {"finite and quaternion rejection", NonFiniteAndQuaternionFailuresAreRejected},
        {"analog and battery rejection", AnalogAndBatteryFailuresAreRejected},
        {"replay and timestamp rejection", ReplayAndTimestampRegressionAreRejected},
        {"new session reset", NewSessionRequiresZeroAndResetsState},
        {"per-hand isolation", PerHandStateIsIsolated},
        {"watchdog neutral safety", WatchdogNeutralizesBothHandsAtFiveHundredMilliseconds},
        {"heartbeat freshness", HeartbeatRefreshesWatchdogWithoutChangingHandState},
        {"invalid input neutral safety", InvalidInputsArePublishedNeutral},
        {"publication boundary neutral safety", PublicationBoundaryCannotLatchInputs},
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
