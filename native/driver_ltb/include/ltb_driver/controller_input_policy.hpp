#pragma once

#include "ltb_driver/protocol.hpp"

#include <cstdint>

namespace ltb::driver {

constexpr bool SystemClickForButtons(Hand hand, std::uint32_t buttons) noexcept {
    return hand == Hand::Left && (buttons & ButtonValue(ButtonBit::Menu)) != 0U;
}

}  // namespace ltb::driver
