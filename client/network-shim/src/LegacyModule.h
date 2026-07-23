#pragma once

#include <Windows.h>

namespace godswar::network {

using LegacyFactory = void*(__cdecl*)();

struct LegacyFactories {
    HMODULE module = nullptr;
    LegacyFactory createClient = nullptr;
    LegacyFactory createService = nullptr;
};

bool LoadVerifiedLegacyModule(
    HMODULE shimModule,
    LegacyFactories* factories) noexcept;

} // namespace godswar::network
