#pragma once

#include "ltb_driver/state_store.hpp"

#include "openvr_driver.h"

#include <array>
#include <cstdint>
#include <string>

namespace ltb::driver::openvr {

class ControllerDevice final : public vr::ITrackedDeviceServerDriver {
public:
    ControllerDevice(StateStore& store, Hand hand);

    vr::EVRInitError Activate(std::uint32_t object_id) override;
    void Deactivate() override;
    void EnterStandby() override;
    void* GetComponent(const char* component_name_and_version) override;
    void DebugRequest(const char* request, char* response, std::uint32_t response_size) override;
    vr::DriverPose_t GetPose() override;

    const std::string& SerialNumber() const noexcept;
    void RunFrame(std::uint64_t now_nanoseconds);

private:
    enum class Input : std::size_t {
        PrimaryClick,
        PrimaryTouch,
        SecondaryClick,
        SecondaryTouch,
        MenuClick,
        TriggerValue,
        TriggerClick,
        TriggerTouch,
        GripValue,
        StickX,
        StickY,
        StickClick,
        StickTouch,
        ThumbrestTouch,
        IndexPointingTouch,
        ThumbUpTouch,
        Count,
    };

    bool CreateBoolean(vr::PropertyContainerHandle_t container, Input input, const char* path);
    bool CreateScalar(
        vr::PropertyContainerHandle_t container,
        Input input,
        const char* path,
        vr::EVRScalarUnits units);
    static bool HasFlag(std::uint16_t flags, StateFlag flag) noexcept;
    static bool HasButton(std::uint32_t buttons, ButtonBit bit) noexcept;
    static bool HasTouch(std::uint32_t touches, TouchBit bit) noexcept;
    vr::DriverPose_t PoseFromSnapshot(
        const HandSnapshot& snapshot,
        std::uint64_t now_nanoseconds) const noexcept;

    StateStore& store_;
    Hand hand_;
    vr::ETrackedControllerRole role_;
    std::string serial_number_;
    vr::TrackedDeviceIndex_t object_id_{vr::k_unTrackedDeviceIndexInvalid};
    vr::PropertyContainerHandle_t property_container_{vr::k_ulInvalidPropertyContainer};
    std::array<vr::VRInputComponentHandle_t, static_cast<std::size_t>(Input::Count)> inputs_{};
};

}  // namespace ltb::driver::openvr
