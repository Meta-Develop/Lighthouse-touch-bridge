#include "device_provider.hpp"

#include "monotonic_clock.hpp"

#include <memory>

namespace ltb::driver::openvr {

vr::EVRInitError DeviceProvider::Init(vr::IVRDriverContext* driver_context) {
    VR_INIT_SERVER_DRIVER_CONTEXT(driver_context);

    receiver_ = std::make_unique<NamedPipeReceiver>(store_);
    if (!receiver_->Start()) {
        receiver_.reset();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        return vr::VRInitError_Driver_Failed;
    }

    left_ = std::make_unique<ControllerDevice>(store_, Hand::Left);
    right_ = std::make_unique<ControllerDevice>(store_, Hand::Right);
    if (!vr::VRServerDriverHost()->TrackedDeviceAdded(
            left_->SerialNumber().c_str(), vr::TrackedDeviceClass_Controller, left_.get()) ||
        !vr::VRServerDriverHost()->TrackedDeviceAdded(
            right_->SerialNumber().c_str(), vr::TrackedDeviceClass_Controller, right_.get())) {
        Cleanup();
        return vr::VRInitError_Driver_Failed;
    }
    return vr::VRInitError_None;
}

void DeviceProvider::Cleanup() {
    if (receiver_ != nullptr) {
        receiver_->Stop();
    }
    receiver_.reset();
    left_.reset();
    right_.reset();
    VR_CLEANUP_SERVER_DRIVER_CONTEXT();
}

const char* const* DeviceProvider::GetInterfaceVersions() {
    return vr::k_InterfaceVersions;
}

void DeviceProvider::RunFrame() {
    const auto now = MonotonicNanoseconds();
    if (left_ != nullptr) {
        left_->RunFrame(now);
    }
    if (right_ != nullptr) {
        right_->RunFrame(now);
    }

    vr::VREvent_t event{};
    while (vr::VRServerDriverHost()->PollNextEvent(&event, sizeof(event))) {
        // driver_ltb has no haptic output component and therefore no event path.
    }
}

bool DeviceProvider::ShouldBlockStandbyMode() {
    return false;
}

void DeviceProvider::EnterStandby() {}

void DeviceProvider::LeaveStandby() {}

}  // namespace ltb::driver::openvr
