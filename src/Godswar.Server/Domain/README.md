# Domain boundary

Domain code owns provider-independent rules, identifiers, value objects, and
results. It must not reference application handlers, persistence providers,
networking, packets, sockets, or the legacy mixed `State` namespace.

Existing domain-like types under `State` move here only as focused feature
slices require them. B02 does not perform a broad namespace rewrite.
