#include "controller_device.hpp"

#include "monotonic_clock.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace ltb::driver::openvr {
namespace {

constexpr char kInputProfile[] = "{ltb}/input/ltb_touch_profile.json";
constexpr char kControllerType[] = "ltb_touch";

template <typename InputType>
std::size_t InputIndex(InputType input) noexcept {
    return static_cast<std::size_t>(input);
}

}  // namespace

ControllerDevice::ControllerDevice(StateStore& store, Hand hand)
    : store_(store),
      hand_(hand),
      role_(hand == Hand::Left
              ? vr::TrackedControllerRole_LeftHand
              : vr::TrackedControllerRole_RightHand),
      serial_number_(hand == Hand::Left ? "LTB-TOUCH-LEFT" : "LTB-TOUCH-RIGHT") {
    inputs_.fill(vr::k_ulInvalidInputComponentHandle);
}

bool ControllerDevice::CreateBoolean(
    vr::PropertyContainerHandle_t container,
    Input input,
    const char* path) {
    return vr::VRDriverInput()->CreateBooleanComponent(
               container, path, &inputs_[InputIndex(input)]) == vr::VRInputError_None;
}

bool ControllerDevice::CreateScalar(
    vr::PropertyContainerHandle_t container,
    Input input,
    const char* path,
    vr::EVRScalarUnits units) {
    return vr::VRDriverInput()->CreateScalarComponent(
               container,
               path,
               &inputs_[InputIndex(input)],
               vr::VRScalarType_Absolute,
               units) == vr::VRInputError_None;
}

vr::EVRInitError ControllerDevice::Activate(std::uint32_t object_id) {
    object_id_ = object_id;
    property_container_ = vr::VRProperties()->TrackedDeviceToPropertyContainer(object_id_);
    vr::VRProperties()->SetStringProperty(property_container_, vr::Prop_TrackingSystemName_String, "ltb");
    vr::VRProperties()->SetStringProperty(
        property_container_, vr::Prop_ModelNumber_String, "Lighthouse Touch Bridge Controller");
    vr::VRProperties()->SetStringProperty(
        property_container_, vr::Prop_SerialNumber_String, serial_number_.c_str());
    vr::VRProperties()->SetStringProperty(
        property_container_, vr::Prop_ManufacturerName_String, "Meta-Develop");
    vr::VRProperties()->SetStringProperty(
        property_container_, vr::Prop_ControllerType_String, kControllerType);
    vr::VRProperties()->SetStringProperty(
        property_container_, vr::Prop_InputProfilePath_String, kInputProfile);
    vr::VRProperties()->SetInt32Property(
        property_container_, vr::Prop_ControllerRoleHint_Int32, static_cast<std::int32_t>(role_));
    vr::VRProperties()->SetBoolProperty(property_container_, vr::Prop_WillDriftInYaw_Bool, false);
    vr::VRProperties()->SetBoolProperty(
        property_container_, vr::Prop_DeviceProvidesBatteryStatus_Bool, false);

    const bool left = hand_ == Hand::Left;
    const bool inputs_created =
        CreateBoolean(property_container_, Input::PrimaryClick, left ? "/input/x/click" : "/input/a/click") &&
        CreateBoolean(property_container_, Input::PrimaryTouch, left ? "/input/x/touch" : "/input/a/touch") &&
        CreateBoolean(property_container_, Input::SecondaryClick, left ? "/input/y/click" : "/input/b/click") &&
        CreateBoolean(property_container_, Input::SecondaryTouch, left ? "/input/y/touch" : "/input/b/touch") &&
        CreateBoolean(property_container_, Input::MenuClick, "/input/menu/click") &&
        CreateScalar(
            property_container_,
            Input::TriggerValue,
            "/input/trigger/value",
            vr::VRScalarUnits_NormalizedOneSided) &&
        CreateBoolean(property_container_, Input::TriggerClick, "/input/trigger/click") &&
        CreateBoolean(property_container_, Input::TriggerTouch, "/input/trigger/touch") &&
        CreateScalar(
            property_container_,
            Input::GripValue,
            "/input/grip/value",
            vr::VRScalarUnits_NormalizedOneSided) &&
        CreateScalar(
            property_container_, Input::StickX, "/input/thumbstick/x", vr::VRScalarUnits_NormalizedTwoSided) &&
        CreateScalar(
            property_container_, Input::StickY, "/input/thumbstick/y", vr::VRScalarUnits_NormalizedTwoSided) &&
        CreateBoolean(property_container_, Input::StickClick, "/input/thumbstick/click") &&
        CreateBoolean(property_container_, Input::StickTouch, "/input/thumbstick/touch") &&
        CreateBoolean(property_container_, Input::ThumbrestTouch, "/input/thumbrest/touch") &&
        CreateBoolean(property_container_, Input::IndexPointingTouch, "/input/index_pointing/touch") &&
        CreateBoolean(property_container_, Input::ThumbUpTouch, "/input/thumb_up/touch");

    return inputs_created ? vr::VRInitError_None : vr::VRInitError_Driver_Failed;
}

void ControllerDevice::Deactivate() {
    object_id_ = vr::k_unTrackedDeviceIndexInvalid;
    property_container_ = vr::k_ulInvalidPropertyContainer;
    inputs_.fill(vr::k_ulInvalidInputComponentHandle);
}

void ControllerDevice::EnterStandby() {}

void* ControllerDevice::GetComponent(const char* component_name_and_version) {
    static_cast<void>(component_name_and_version);
    return nullptr;
}

void ControllerDevice::DebugRequest(
    const char* request,
    char* response,
    std::uint32_t response_size) {
    static_cast<void>(request);
    if (response != nullptr && response_size > 0U) {
        response[0] = '\0';
    }
}

bool ControllerDevice::HasFlag(std::uint16_t flags, StateFlag flag) noexcept {
    return (flags & FlagValue(flag)) != 0U;
}

bool ControllerDevice::HasButton(std::uint32_t buttons, ButtonBit bit) noexcept {
    return (buttons & ButtonValue(bit)) != 0U;
}

bool ControllerDevice::HasTouch(std::uint32_t touches, TouchBit bit) noexcept {
    return (touches & TouchValue(bit)) != 0U;
}

vr::DriverPose_t ControllerDevice::PoseFromSnapshot(
    const HandSnapshot& snapshot,
    std::uint64_t now_nanoseconds) const noexcept {
    vr::DriverPose_t pose{};
    pose.qWorldFromDriverRotation.w = 1.0;
    pose.qDriverFromHeadRotation.w = 1.0;
    pose.qRotation.w = 1.0;
    pose.deviceIsConnected = true;
    pose.result = vr::TrackingResult_Running_OutOfRange;

    const auto& state = snapshot.state;
    if (!IsPosePublishable(snapshot)) {
        return pose;
    }

    pose.poseIsValid = true;
    pose.result = vr::TrackingResult_Running_OK;
    pose.vecPosition[0] = state.position.x;
    pose.vecPosition[1] = state.position.y;
    pose.vecPosition[2] = state.position.z;
    pose.qRotation.x = state.orientation.x;
    pose.qRotation.y = state.orientation.y;
    pose.qRotation.z = state.orientation.z;
    pose.qRotation.w = state.orientation.w;

    if (HasFlag(state.flags, StateFlag::LinearVelocityValid)) {
        pose.vecVelocity[0] = state.linear_velocity.x;
        pose.vecVelocity[1] = state.linear_velocity.y;
        pose.vecVelocity[2] = state.linear_velocity.z;
    }
    if (HasFlag(state.flags, StateFlag::AngularVelocityValid)) {
        pose.vecAngularVelocity[0] = state.angular_velocity.x;
        pose.vecAngularVelocity[1] = state.angular_velocity.y;
        pose.vecAngularVelocity[2] = state.angular_velocity.z;
    }
    if (state.source_timestamp_nanoseconds <= now_nanoseconds) {
        const auto age_nanoseconds = now_nanoseconds - state.source_timestamp_nanoseconds;
        pose.poseTimeOffset = -static_cast<double>(age_nanoseconds) / 1'000'000'000.0;
    }
    return pose;
}

vr::DriverPose_t ControllerDevice::GetPose() {
    const auto now = MonotonicNanoseconds();
    return PoseFromSnapshot(store_.Snapshot(hand_, now), now);
}

const std::string& ControllerDevice::SerialNumber() const noexcept {
    return serial_number_;
}

void ControllerDevice::RunFrame(std::uint64_t now_nanoseconds) {
    if (object_id_ == vr::k_unTrackedDeviceIndexInvalid) {
        return;
    }
    const auto snapshot = store_.Snapshot(hand_, now_nanoseconds);
    const auto pose = PoseFromSnapshot(snapshot, now_nanoseconds);
    vr::VRServerDriverHost()->TrackedDevicePoseUpdated(object_id_, pose, sizeof(pose));

    const auto& state = snapshot.state;
    const auto published_input = InputForPublication(snapshot);
    const double input_time_offset = pose.poseTimeOffset;
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::PrimaryClick)],
        HasButton(published_input.buttons, ButtonBit::Primary),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::PrimaryTouch)],
        HasTouch(published_input.touches, TouchBit::Primary),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::SecondaryClick)],
        HasButton(published_input.buttons, ButtonBit::Secondary),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::SecondaryTouch)],
        HasTouch(published_input.touches, TouchBit::Secondary),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::MenuClick)],
        HasButton(published_input.buttons, ButtonBit::Menu),
        input_time_offset);
    vr::VRDriverInput()->UpdateScalarComponent(
        inputs_[InputIndex(Input::TriggerValue)], published_input.trigger, input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::TriggerClick)],
        HasButton(published_input.buttons, ButtonBit::TriggerClick),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::TriggerTouch)],
        HasTouch(published_input.touches, TouchBit::Trigger),
        input_time_offset);
    vr::VRDriverInput()->UpdateScalarComponent(
        inputs_[InputIndex(Input::GripValue)], published_input.grip, input_time_offset);
    vr::VRDriverInput()->UpdateScalarComponent(
        inputs_[InputIndex(Input::StickX)], published_input.stick_x, input_time_offset);
    vr::VRDriverInput()->UpdateScalarComponent(
        inputs_[InputIndex(Input::StickY)], published_input.stick_y, input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::StickClick)],
        HasButton(published_input.buttons, ButtonBit::StickClick),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::StickTouch)],
        HasTouch(published_input.touches, TouchBit::Stick),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::ThumbrestTouch)],
        HasTouch(published_input.touches, TouchBit::Thumbrest),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::IndexPointingTouch)],
        HasTouch(published_input.touches, TouchBit::IndexPointing),
        input_time_offset);
    vr::VRDriverInput()->UpdateBooleanComponent(
        inputs_[InputIndex(Input::ThumbUpTouch)],
        HasTouch(published_input.touches, TouchBit::ThumbUp),
        input_time_offset);

    const bool battery_present = !snapshot.stale && HasFlag(state.flags, StateFlag::BatteryPresent);
    vr::VRProperties()->SetBoolProperty(
        property_container_, vr::Prop_DeviceProvidesBatteryStatus_Bool, battery_present);
    if (battery_present) {
        vr::VRProperties()->SetFloatProperty(
            property_container_, vr::Prop_DeviceBatteryPercentage_Float, state.battery);
    }
}

}  // namespace ltb::driver::openvr
