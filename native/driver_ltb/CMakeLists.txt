cmake_minimum_required(VERSION 3.24)

project(driver_ltb VERSION 0.1.0 LANGUAGES CXX)

option(LTB_BUILD_OPENVR_DRIVER "Build the Windows OpenVR driver shell" ${WIN32})
option(LTB_BUILD_TESTS "Build the portable driver-core tests" ON)

if(MSVC)
    # Keep the staged driver independent of the separately installed MSVC runtime.
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

if(LTB_BUILD_OPENVR_DRIVER OR LTB_BUILD_TESTS)
    find_package(Python3 REQUIRED COMPONENTS Interpreter)
endif()

set(LTB_IPC_PROTOCOL_MAJOR 1)
set(LTB_IPC_PROTOCOL_MINOR 0)
set(LTB_DRIVER_VERSION "${PROJECT_VERSION}")
set(LTB_IPC_PROTOCOL_VERSION "${LTB_IPC_PROTOCOL_MAJOR}.${LTB_IPC_PROTOCOL_MINOR}")
set(LTB_BUILD_IDENTITY
    "driver_ltb-${LTB_DRIVER_VERSION}-ipc-${LTB_IPC_PROTOCOL_VERSION}")

if(NOT LTB_DRIVER_VERSION MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+$")
    message(FATAL_ERROR "driver_ltb version must use numeric major.minor.patch format")
endif()
if(NOT LTB_IPC_PROTOCOL_VERSION MATCHES "^[0-9]+\\.[0-9]+$")
    message(FATAL_ERROR "driver_ltb IPC version must use numeric major.minor format")
endif()
if(NOT LTB_BUILD_IDENTITY MATCHES
        "^driver_ltb-[0-9]+\\.[0-9]+\\.[0-9]+-ipc-[0-9]+\\.[0-9]+$")
    message(FATAL_ERROR "driver_ltb build identity has an invalid format")
endif()

set(LTB_GENERATED_INCLUDE_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated/include")
set(LTB_DRIVER_STAGE "${CMAKE_BINARY_DIR}/driver_ltb")
set(LTB_STAGED_BUILD_ID_FILE "${LTB_DRIVER_STAGE}/build-id.txt")
set(LTB_STAGED_MANIFEST "${LTB_DRIVER_STAGE}/driver.vrdrivermanifest")
file(MAKE_DIRECTORY "${LTB_GENERATED_INCLUDE_DIR}/ltb_driver" "${LTB_DRIVER_STAGE}")
configure_file(
    cmake/build_identity.hpp.in
    "${LTB_GENERATED_INCLUDE_DIR}/ltb_driver/build_identity.hpp"
    @ONLY)
configure_file(
    cmake/build-id.txt.in
    "${LTB_STAGED_BUILD_ID_FILE}"
    @ONLY
    NEWLINE_STYLE UNIX)

add_custom_target(ltb_driver_stage_assets ALL
    COMMAND "${CMAKE_COMMAND}" -E copy_directory
        "${CMAKE_CURRENT_SOURCE_DIR}/driver"
        "${LTB_DRIVER_STAGE}"
    BYPRODUCTS "${LTB_STAGED_MANIFEST}"
    COMMENT "Staging driver_ltb SteamVR manifest and resources"
    VERBATIM)

add_library(ltb_driver_core STATIC
    src/core/peer_session.cpp
    src/core/protocol.cpp
    src/core/state_store.cpp)
target_include_directories(ltb_driver_core PUBLIC
    include
    "${LTB_GENERATED_INCLUDE_DIR}")
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

    add_executable(ltb_driver_retry_backoff_tests tests/retry_backoff_tests.cpp)
    target_link_libraries(ltb_driver_retry_backoff_tests PRIVATE ltb_driver_core)
    target_compile_features(ltb_driver_retry_backoff_tests PRIVATE cxx_std_20)
    if(MSVC)
        target_compile_options(
            ltb_driver_retry_backoff_tests PRIVATE /W4 /WX /permissive-)
    else()
        target_compile_options(
            ltb_driver_retry_backoff_tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME ltb_driver_retry_backoff_tests COMMAND ltb_driver_retry_backoff_tests)

    add_executable(ltb_driver_build_identity_tests tests/build_identity_tests.cpp)
    target_link_libraries(ltb_driver_build_identity_tests PRIVATE ltb_driver_core)
    target_compile_features(ltb_driver_build_identity_tests PRIVATE cxx_std_20)
    add_dependencies(ltb_driver_build_identity_tests ltb_driver_stage_assets)
    if(MSVC)
        target_compile_options(
            ltb_driver_build_identity_tests PRIVATE /W4 /WX /permissive-)
    else()
        target_compile_options(
            ltb_driver_build_identity_tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(
        NAME ltb_driver_build_identity_tests
        COMMAND ltb_driver_build_identity_tests
            "${LTB_STAGED_BUILD_ID_FILE}"
            "${LTB_STAGED_MANIFEST}")

    add_test(
        NAME ltb_driver_pe_import_checker_tests
        COMMAND "${Python3_EXECUTABLE}"
            "${CMAKE_CURRENT_SOURCE_DIR}/tests/check_pe_imports_tests.py")
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
        src/openvr
        "${LTB_GENERATED_INCLUDE_DIR}")
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
    if(MINGW)
        # Both MinGW GCC and LLVM-MinGW honor -static for their C++ and support
        # runtimes while retaining Windows system DLL imports through import libs.
        target_link_options(driver_ltb PRIVATE -static)
        if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU")
            target_link_options(driver_ltb PRIVATE -static-libgcc -static-libstdc++)
        endif()
    endif()

    set_target_properties(driver_ltb PROPERTIES
        PREFIX ""
        OUTPUT_NAME "driver_ltb"
        RUNTIME_OUTPUT_DIRECTORY "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${LTB_DRIVER_STAGE}/bin/win64"
        RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${LTB_DRIVER_STAGE}/bin/win64")
    add_dependencies(driver_ltb ltb_driver_stage_assets)

    add_custom_target(ltb_driver_verify_imports ALL
        COMMAND "${Python3_EXECUTABLE}"
            "${CMAKE_CURRENT_SOURCE_DIR}/tools/check_pe_imports.py"
            --binary "$<TARGET_FILE:driver_ltb>"
        DEPENDS driver_ltb
        COMMENT "Checking driver_ltb PE imports"
        VERBATIM)
endif()
