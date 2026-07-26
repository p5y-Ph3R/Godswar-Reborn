#pragma once

#include <Windows.h>

#include <cstdint>
#include <string>
#include <vector>

namespace snapshot_inspector {

struct RuntimeSymbol final {
    DWORD64 address = 0;
    ULONG typeId = 0;
    ULONG size = 0;
    ULONG tag = 0;
    std::wstring name;
    std::wstring typeName;
};

struct Field final {
    ULONG typeId = 0;
    ULONG offset = 0;
    ULONG64 length = 0;
};

bool FindRuntimeSymbol(
    HANDLE process,
    DWORD64 moduleBase,
    RuntimeSymbol* runtime,
    std::vector<RuntimeSymbol>* observations,
    std::size_t* matchCount);

bool FindMember(
    HANDLE process,
    DWORD64 moduleBase,
    ULONG parentType,
    const wchar_t* memberName,
    Field* field);

bool ReadStableSnapshot(
    HANDLE process,
    DWORD64 runtimeAddress,
    const Field& lockField,
    const Field& snapshotField,
    std::vector<std::uint8_t>* snapshot,
    unsigned* attempts);

bool DumpSnapshotType(
    HANDLE process,
    DWORD64 moduleBase,
    ULONG originalTypeId,
    const std::uint8_t* snapshot,
    ULONG64 snapshotBytes,
    ULONG baseOffset,
    const std::wstring& path);

} // namespace snapshot_inspector
