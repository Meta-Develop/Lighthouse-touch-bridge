#pragma once

#include "controller_device.hpp"
#include "named_pipe_receiver.hpp"

#include "ltb_driver/state_store.hpp"

#include "openvr_driver.h"

#include <memory>

namespace ltb::driver::openvr {

class DeviceProvider final : public vr::IServerTrackedDeviceProvider {
public:
    vr::EVRInitError Init(vr::IVRDriverContext* driver_context) override;
    void Cleanup() override;
    const char* const* GetInterfaceVersions() override;
    void RunFrame() override;
    bool ShouldBlockStandbyMode() override;
    void EnterStandby() override;
    void LeaveStandby() override;

private:
    StateStore store_;
    std::unique_ptr<NamedPipeReceiver> receiver_;
    std::unique_ptr<ControllerDevice> left_;
    std::unique_ptr<ControllerDevice> right_;
};

}  // namespace ltb::driver::openvr
