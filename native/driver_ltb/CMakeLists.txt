cmake_minimum_required(VERSION 3.24)

project(driver_ltb LANGUAGES CXX)

option(LTB_BUILD_OPENVR_DRIVER "Build the Windows OpenVR driver shell" ${WIN32})
option(LTB_BUILD_TESTS "Build the portable driver-core tests" ON)

add_library(ltb_driver_core STATIC
    src/core/protocol.cpp
    src/core/state_store.cpp)
target_include_directories(ltb_driver_core PUBLIC include)
target_compile_features(ltb_driver_core PUBLIC cxx_std_20)

if(MSVC)
    target_compile_options(ltb_driver_core PRIVATE /W4 /WX /permissive-)
else()
    target_compile_options(ltb_driver_core PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

if(LTB_BUILD_TESTS)
    enable_testing()
    add_executable(ltb_driver_core_tests tests/core_tests.cpp)
    target_link_libraries(ltb_driver_core_tests PRIVATE ltb_driver_core)
    target_compile_features(ltb_driver_core_tests PRIVATE cxx_std_20)
    if(MSVC)
        target_compile_options(ltb_driver_core_tests PRIVATE /W4 /WX /permissive-)
    else()
        target_compile_options(ltb_driver_core_tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME ltb_driver_core_tests COMMAND ltb_driver_core_tests)
endif()

if(LTB_BUILD_OPENVR_DRIVER)
    if(NOT WIN32)
        message(FATAL_ERROR "LTB_BUILD_OPENVR_DRIVER requires Windows")
    endif()

    set(LTB_OPENVR_HEADER_DIR
        "${CMAKE_CURRENT_SOURCE_DIR}/../../src/Ltb.OpenVr/ThirdParty/OpenVR"
        CACHE PATH "Directory containing the pinned openvr_driver.h")
    if(NOT EXISTS "${LTB_OPENVR_HEADER_DIR}/openvr_driver.h")
        message(FATAL_ERROR "Pinned openvr_driver.h not found in ${LTB_OPENVR_HEADER_DIR}")
    endif()

    add_library(driver_ltb SHARED
        src/openvr/controller_device.cpp
        src/openvr/device_provider.cpp
        src/openvr/hmd_driver_factory.cpp
        src/openvr/named_pipe_receiver.cpp)
    target_compile_features(driver_ltb PRIVATE cxx_std_20)
    target_include_directories(driver_ltb PRIVATE
        include
        src/openvr)
    target_include_directories(driver_ltb SYSTEM PRIVATE "${LTB_OPENVR_HEADER_DIR}")
    target_link_libraries(driver_ltb PRIVATE ltb_driver_core advapi32)
    target_compile_definitions(driver_ltb PRIVATE
        WIN32_LEAN_AND_MEAN
        NOMINMAX
        _WIN32_WINNT=0x0A00)
    if(MSVC)
        target_compile_options(driver_ltb PRIVATE /W4 /WX /permissive- /EHsc)
    else()
        target_compile_options(driver_ltb PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()

    set(LTB_DRIVER_STAGE "${CMAKE_BINARY_DIR}/driver_ltb")
    set_target_properties(driver_ltb PROPERTIES
        PREFIX ""
        OUTPUT_NAME "driver_ltb"
        RUNTIME_OUTPUT_DIRECTORY "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${LTB_DRIVER_STAGE}/bin/win64")

    add_custom_command(TARGET driver_ltb POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_directory
            "${CMAKE_CURRENT_SOURCE_DIR}/driver"
            "${LTB_DRIVER_STAGE}"
        COMMENT "Staging driver_ltb SteamVR manifest and resources")
endif()
