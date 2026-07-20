#include "ltb_driver/build_identity.hpp"

#include <cctype>
#include <cstddef>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <string_view>

namespace {

void Require(bool condition, const std::string& message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

bool HasNumericComponents(std::string_view value, std::size_t component_count) {
    if (value.empty() || component_count == 0U) {
        return false;
    }

    std::size_t component = 1U;
    bool has_digit = false;
    for (const char character : value) {
        if (character == '.') {
            if (!has_digit || component == component_count) {
                return false;
            }
            ++component;
            has_digit = false;
            continue;
        }
        if (std::isdigit(static_cast<unsigned char>(character)) == 0) {
            return false;
        }
        has_digit = true;
    }
    return has_digit && component == component_count;
}

std::string ReadFile(const std::filesystem::path& path) {
    std::ifstream stream(path, std::ios::binary);
    Require(stream.good(), "could not open generated file: " + path.string());
    return {std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
}

void VerifyBuildIdentity(
    const std::filesystem::path& build_id_path,
    const std::filesystem::path& manifest_path) {
    const std::string_view driver_version{ltb::driver::kDriverVersion};
    const std::string_view protocol_version{ltb::driver::kProtocolVersion};
    const std::string_view build_identity{ltb::driver::kBuildIdentity};

    Require(HasNumericComponents(driver_version, 3U), "driver version is not major.minor.patch");
    Require(HasNumericComponents(protocol_version, 2U), "IPC version is not major.minor");
    Require(!build_identity.empty(), "build identity is blank");

    const auto expected_protocol = std::to_string(ltb::driver::kProtocolMajor) + "." +
        std::to_string(ltb::driver::kProtocolMinor);
    Require(protocol_version == expected_protocol, "IPC numeric and string versions drifted");

    const auto expected_identity =
        std::string{"driver_ltb-"} + std::string{driver_version} + "-ipc-" + expected_protocol;
    Require(build_identity == expected_identity, "build identity does not match its components");

    Require(build_id_path.filename() == "build-id.txt", "staged marker has the wrong name");
    Require(
        manifest_path.filename() == "driver.vrdrivermanifest",
        "staged manifest has the wrong name");
    Require(
        build_id_path.parent_path().lexically_normal() ==
            manifest_path.parent_path().lexically_normal(),
        "build identity is not staged beside the driver manifest");
    Require(std::filesystem::is_regular_file(manifest_path), "staged driver manifest is missing");

    const auto marker = ReadFile(build_id_path);
    Require(!marker.empty(), "staged build identity is blank");
    Require(marker == expected_identity + '\n', "staged and compile-time build identities drifted");
}

}  // namespace

int main(int argc, char* argv[]) {
    if (argc != 3) {
        std::cerr << "expected build-id.txt and driver.vrdrivermanifest paths\n";
        return 1;
    }

    try {
        VerifyBuildIdentity(argv[1], argv[2]);
        std::cout << "PASS: " << ltb::driver::kBuildIdentity << '\n';
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
}
