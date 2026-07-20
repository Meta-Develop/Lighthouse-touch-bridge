#include "device_provider.hpp"

#include "openvr_driver.h"

#include <cstring>

namespace {

ltb::driver::openvr::DeviceProvider g_device_provider;

}  // namespace

extern "C" __declspec(dllexport) void* HmdDriverFactory(
    const char* interface_name,
    int* return_code) {
    if (interface_name != nullptr &&
        std::strcmp(interface_name, vr::IServerTrackedDeviceProvider_Version) == 0) {
        return &g_device_provider;
    }
    if (return_code != nullptr) {
        *return_code = vr::VRInitError_Init_InterfaceNotFound;
    }
    return nullptr;
}
