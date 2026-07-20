#pragma once

#include "ltb_driver/state_store.hpp"

#include <atomic>
#include <thread>

namespace ltb::driver::openvr {

class NamedPipeReceiver final {
public:
    explicit NamedPipeReceiver(StateStore& store) noexcept;
    ~NamedPipeReceiver();

    NamedPipeReceiver(const NamedPipeReceiver&) = delete;
    NamedPipeReceiver& operator=(const NamedPipeReceiver&) = delete;

    bool Start();
    void Stop() noexcept;

private:
    void Run() noexcept;

    StateStore& store_;
    std::atomic<bool> stop_requested_{false};
    std::thread thread_;
};

}  // namespace ltb::driver::openvr
