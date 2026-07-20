#pragma once

#include "ltb_driver/state_store.hpp"

#include <cstdint>
#include <span>

namespace ltb::driver {

enum class PeerSessionLookupStatus {
    Resolved,
    ClientProcessLookupFailed,
    ClientSessionLookupFailed,
    ServerSessionLookupFailed,
};

struct PeerSessionEvidence {
    PeerSessionLookupStatus lookup_status{PeerSessionLookupStatus::ClientProcessLookupFailed};
    std::uint32_t client_session_id{};
    std::uint32_t server_session_id{};
};

enum class PeerSessionAuthorization {
    Authorized,
    ClientProcessLookupFailed,
    ClientSessionLookupFailed,
    ServerSessionLookupFailed,
    SessionMismatch,
};

PeerSessionAuthorization VerifyPeerSession(const PeerSessionEvidence& evidence) noexcept;
const char* PeerSessionAuthorizationDiagnostic(PeerSessionAuthorization result) noexcept;

class PeerSessionPacketGate final {
public:
    PeerSessionPacketGate(StateStore& store, const PeerSessionEvidence& evidence) noexcept;

    ApplyError ApplyPacket(
        std::span<const std::uint8_t> packet,
        std::uint64_t arrival_nanoseconds) const noexcept;
    PeerSessionAuthorization Authorization() const noexcept;
    bool IsAuthorized() const noexcept;

private:
    StateStore& store_;
    PeerSessionAuthorization authorization_;
};

}  // namespace ltb::driver
