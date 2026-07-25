#include "ltb_driver/peer_session.hpp"

namespace ltb::driver {

PeerSessionAuthorization VerifyPeerSession(const PeerSessionEvidence& evidence) noexcept {
    switch (evidence.lookup_status) {
        case PeerSessionLookupStatus::ClientProcessLookupFailed:
            return PeerSessionAuthorization::ClientProcessLookupFailed;
        case PeerSessionLookupStatus::ClientSessionLookupFailed:
            return PeerSessionAuthorization::ClientSessionLookupFailed;
        case PeerSessionLookupStatus::ServerSessionLookupFailed:
            return PeerSessionAuthorization::ServerSessionLookupFailed;
        case PeerSessionLookupStatus::Resolved:
            return evidence.client_session_id == evidence.server_session_id
                ? PeerSessionAuthorization::Authorized
                : PeerSessionAuthorization::SessionMismatch;
    }
    return PeerSessionAuthorization::ClientProcessLookupFailed;
}

const char* PeerSessionAuthorizationDiagnostic(PeerSessionAuthorization result) noexcept {
    switch (result) {
        case PeerSessionAuthorization::Authorized:
            return "driver_ltb: named-pipe client authorized for the current Windows session\n";
        case PeerSessionAuthorization::ClientProcessLookupFailed:
            return "driver_ltb: rejected named-pipe client: client process lookup failed\n";
        case PeerSessionAuthorization::ClientSessionLookupFailed:
            return "driver_ltb: rejected named-pipe client: client session lookup failed\n";
        case PeerSessionAuthorization::ServerSessionLookupFailed:
            return "driver_ltb: rejected named-pipe client: server session lookup failed\n";
        case PeerSessionAuthorization::SessionMismatch:
            return "driver_ltb: rejected named-pipe client: Windows session mismatch\n";
    }
    return "driver_ltb: rejected named-pipe client: client process lookup failed\n";
}

PeerSessionPacketGate::PeerSessionPacketGate(
    StateStore& store,
    const PeerSessionEvidence& evidence) noexcept
    : store_(store), authorization_(VerifyPeerSession(evidence)) {}

ApplyError PeerSessionPacketGate::ApplyPacket(
    std::span<const std::uint8_t> packet,
    std::uint64_t arrival_nanoseconds) const noexcept {
    if (!IsAuthorized()) {
        return ApplyError::PeerNotAuthorized;
    }
    return store_.ApplyPacket(packet, arrival_nanoseconds);
}

PeerSessionAuthorization PeerSessionPacketGate::Authorization() const noexcept {
    return authorization_;
}

bool PeerSessionPacketGate::IsAuthorized() const noexcept {
    return authorization_ == PeerSessionAuthorization::Authorized;
}

}  // namespace ltb::driver
