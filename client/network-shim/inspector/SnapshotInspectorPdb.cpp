#include "SnapshotInspectorPdb.h"

#define _NO_CVCONST_H
#include <DbgHelp.h>

#include <algorithm>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <iomanip>
#include <iostream>

namespace snapshot_inspector {
namespace {

constexpr DWORD PdbBaseTypeInt = 6;
constexpr DWORD PdbBaseTypeUInt = 7;
constexpr DWORD PdbBaseTypeLong = 13;

struct EnumContext final {
    HANDLE process = nullptr;
    DWORD64 moduleBase = 0;
    std::vector<RuntimeSymbol>* candidates = nullptr;
    std::vector<RuntimeSymbol>* observations = nullptr;
};

bool TypeName(
    HANDLE process,
    DWORD64 moduleBase,
    ULONG typeId,
    std::wstring* name) {
    wchar_t* rawName = nullptr;
    if (!SymGetTypeInfo(
            process,
            moduleBase,
            typeId,
            TI_GET_SYMNAME,
            &rawName)) {
        return false;
    }
    *name = rawName != nullptr ? rawName : L"";
    LocalFree(rawName);
    return true;
}

bool EndsWith(const std::wstring& value, const wchar_t* suffix) {
    const std::size_t suffixLength = std::wcslen(suffix);
    return value.size() >= suffixLength &&
        value.compare(
            value.size() - suffixLength,
            suffixLength,
            suffix) == 0;
}

BOOL CALLBACK CollectRuntimeSymbol(
    PSYMBOL_INFOW symbol,
    ULONG,
    void* opaque) {
    auto* context = static_cast<EnumContext*>(opaque);
    if (symbol == nullptr) {
        return TRUE;
    }
    std::wstring lowered = symbol->Name;
    std::transform(
        lowered.begin(),
        lowered.end(),
        lowered.begin(),
        towlower);
    std::wstring typeName;
    static_cast<void>(TypeName(
        context->process,
        context->moduleBase,
        symbol->TypeIndex,
        &typeName));
    if (lowered.find(L"runtime") != std::wstring::npos) {
        context->observations->push_back(RuntimeSymbol{
            symbol->Address,
            symbol->TypeIndex,
            symbol->Size,
            symbol->Tag,
            symbol->Name,
            typeName});
    }
    if (symbol->Tag != SymTagData ||
        symbol->Address < context->moduleBase ||
        !EndsWith(typeName, L"SecureClientRuntime")) {
        return TRUE;
    }
    context->candidates->push_back(RuntimeSymbol{
        symbol->Address,
        symbol->TypeIndex,
        symbol->Size,
        symbol->Tag,
        symbol->Name,
        typeName});
    return TRUE;
}

bool IsAllowedSnapshotType(const std::wstring& name) {
    constexpr const wchar_t* allowed[] = {
        L"SecureClientSessionRetentionSnapshot",
        L"SecureClientSessionSnapshot",
        L"ExternalTcpConnectSnapshot",
        L"SchannelClientSnapshot",
        L"SecureOuterSnapshot",
        L"NativeClientBridgeSnapshot",
        L"OpaqueDuplexPumpSnapshot",
        L"BoundedChunkQueueSnapshot",
        L"SecureUdpClientWorkerSnapshot",
        L"SecureUdpClientChannelSnapshot",
        L"SecureRealtimeMovementRouterSnapshot",
    };
    return std::any_of(
        std::begin(allowed),
        std::end(allowed),
        [&name](const wchar_t* suffix) {
            return EndsWith(name, suffix);
        });
}

bool UnwrapType(
    HANDLE process,
    DWORD64 moduleBase,
    ULONG* typeId,
    DWORD* tag) {
    for (unsigned attempt = 0; attempt < 8; ++attempt) {
        if (!SymGetTypeInfo(
                process,
                moduleBase,
                *typeId,
                TI_GET_SYMTAG,
                tag)) {
            return false;
        }
        if (*tag != SymTagTypedef) {
            return true;
        }
        ULONG next = 0;
        if (!SymGetTypeInfo(
                process,
                moduleBase,
                *typeId,
                TI_GET_TYPEID,
                &next)) {
            return false;
        }
        *typeId = next;
    }
    SetLastError(ERROR_INVALID_DATA);
    return false;
}

std::uint64_t ReadUnsigned(
    const std::uint8_t* bytes,
    ULONG64 length) {
    std::uint64_t value = 0;
    std::memcpy(
        &value,
        bytes,
        static_cast<std::size_t>(
            std::min<ULONG64>(length, sizeof(value))));
    return value;
}

std::int64_t SignExtend(std::uint64_t value, ULONG64 length) {
    if (length == 1) {
        return static_cast<std::int8_t>(value);
    }
    if (length == 2) {
        return static_cast<std::int16_t>(value);
    }
    if (length == 4) {
        return static_cast<std::int32_t>(value);
    }
    return static_cast<std::int64_t>(value);
}

} // namespace

bool FindRuntimeSymbol(
    HANDLE process,
    DWORD64 moduleBase,
    RuntimeSymbol* runtime,
    std::vector<RuntimeSymbol>* observations,
    std::size_t* matchCount) {
    if (process == nullptr ||
        runtime == nullptr ||
        observations == nullptr ||
        matchCount == nullptr) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    std::vector<RuntimeSymbol> candidates;
    EnumContext context{
        process,
        moduleBase,
        &candidates,
        observations};
    bool enumerated = SymEnumSymbolsW(
        process,
        moduleBase,
        nullptr,
        CollectRuntimeSymbol,
        &context) != FALSE;
    if (enumerated && candidates.empty()) {
        const auto function = std::find_if(
            observations->begin(),
            observations->end(),
            [](const RuntimeSymbol& symbol) {
                return symbol.tag == SymTagFunction &&
                    EndsWith(
                        symbol.name,
                        L"ProcessSecureClientRuntime");
            });
        if (function != observations->end()) {
            IMAGEHLP_STACK_FRAME frame{};
            frame.InstructionOffset = function->address;
            if (SymSetContext(process, &frame, nullptr)) {
                observations->clear();
                enumerated = SymEnumSymbolsW(
                    process,
                    0,
                    nullptr,
                    CollectRuntimeSymbol,
                    &context) != FALSE;
            }
        }
    }

    *matchCount = candidates.size();
    if (!enumerated || candidates.size() != 1) {
        SetLastError(ERROR_NOT_FOUND);
        return false;
    }
    *runtime = candidates.front();
    return true;
}

bool FindMember(
    HANDLE process,
    DWORD64 moduleBase,
    ULONG parentType,
    const wchar_t* memberName,
    Field* field) {
    DWORD count = 0;
    if (!SymGetTypeInfo(
            process,
            moduleBase,
            parentType,
            TI_GET_CHILDRENCOUNT,
            &count)) {
        return false;
    }
    const std::size_t bytes =
        sizeof(TI_FINDCHILDREN_PARAMS) +
        (count == 0 ? 0 : (count - 1) * sizeof(ULONG));
    std::vector<std::uint8_t> storage(bytes);
    auto* children =
        reinterpret_cast<TI_FINDCHILDREN_PARAMS*>(storage.data());
    children->Count = count;
    children->Start = 0;
    if (count != 0 &&
        !SymGetTypeInfo(
            process,
            moduleBase,
            parentType,
            TI_FINDCHILDREN,
            children)) {
        return false;
    }

    for (DWORD index = 0; index < count; ++index) {
        const ULONG child = children->ChildId[index];
        DWORD tag = 0;
        if (!SymGetTypeInfo(
                process,
                moduleBase,
                child,
                TI_GET_SYMTAG,
                &tag) ||
            tag != SymTagData) {
            continue;
        }
        std::wstring name;
        ULONG offset = 0;
        ULONG typeId = 0;
        if (!TypeName(process, moduleBase, child, &name) ||
            name != memberName ||
            !SymGetTypeInfo(
                process,
                moduleBase,
                child,
                TI_GET_OFFSET,
                &offset) ||
            !SymGetTypeInfo(
                process,
                moduleBase,
                child,
                TI_GET_TYPEID,
                &typeId)) {
            continue;
        }
        ULONG64 length = 0;
        if (!SymGetTypeInfo(
                process,
                moduleBase,
                typeId,
                TI_GET_LENGTH,
                &length)) {
            return false;
        }
        *field = Field{typeId, offset, length};
        return true;
    }
    SetLastError(ERROR_NOT_FOUND);
    return false;
}

bool DumpSnapshotType(
    HANDLE process,
    DWORD64 moduleBase,
    ULONG originalTypeId,
    const std::uint8_t* snapshot,
    ULONG64 snapshotBytes,
    ULONG baseOffset,
    const std::wstring& path) {
    ULONG typeId = originalTypeId;
    DWORD tag = 0;
    if (!UnwrapType(process, moduleBase, &typeId, &tag) ||
        tag != SymTagUDT) {
        return false;
    }
    std::wstring typeName;
    if (!TypeName(process, moduleBase, typeId, &typeName) ||
        !IsAllowedSnapshotType(typeName)) {
        SetLastError(ERROR_ACCESS_DENIED);
        return false;
    }

    DWORD count = 0;
    if (!SymGetTypeInfo(
            process,
            moduleBase,
            typeId,
            TI_GET_CHILDRENCOUNT,
            &count)) {
        return false;
    }
    const std::size_t childBytes =
        sizeof(TI_FINDCHILDREN_PARAMS) +
        (count == 0 ? 0 : (count - 1) * sizeof(ULONG));
    std::vector<std::uint8_t> storage(childBytes);
    auto* children =
        reinterpret_cast<TI_FINDCHILDREN_PARAMS*>(storage.data());
    children->Count = count;
    children->Start = 0;
    if (count != 0 &&
        !SymGetTypeInfo(
            process,
            moduleBase,
            typeId,
            TI_FINDCHILDREN,
            children)) {
        return false;
    }

    for (DWORD index = 0; index < count; ++index) {
        const ULONG child = children->ChildId[index];
        DWORD childTag = 0;
        ULONG childOffset = 0;
        ULONG childType = 0;
        std::wstring childName;
        if (!SymGetTypeInfo(
                process,
                moduleBase,
                child,
                TI_GET_SYMTAG,
                &childTag) ||
            childTag != SymTagData ||
            !TypeName(process, moduleBase, child, &childName) ||
            !SymGetTypeInfo(
                process,
                moduleBase,
                child,
                TI_GET_OFFSET,
                &childOffset) ||
            !SymGetTypeInfo(
                process,
                moduleBase,
                child,
                TI_GET_TYPEID,
                &childType)) {
            continue;
        }
        ULONG resolvedType = childType;
        DWORD resolvedTag = 0;
        if (!UnwrapType(
                process,
                moduleBase,
                &resolvedType,
                &resolvedTag)) {
            return false;
        }
        ULONG64 length = 0;
        if (!SymGetTypeInfo(
                process,
                moduleBase,
                resolvedType,
                TI_GET_LENGTH,
                &length) ||
            static_cast<ULONG64>(baseOffset) + childOffset + length >
                snapshotBytes) {
            SetLastError(ERROR_INVALID_DATA);
            return false;
        }

        const std::wstring childPath =
            path.empty() ? childName : path + L"." + childName;
        if (resolvedTag == SymTagUDT) {
            if (!DumpSnapshotType(
                    process,
                    moduleBase,
                    resolvedType,
                    snapshot,
                    snapshotBytes,
                    baseOffset + childOffset,
                    childPath)) {
                return false;
            }
            continue;
        }
        if (resolvedTag != SymTagBaseType &&
            resolvedTag != SymTagEnum) {
            SetLastError(ERROR_ACCESS_DENIED);
            return false;
        }

        DWORD baseType = PdbBaseTypeUInt;
        if (resolvedTag == SymTagBaseType &&
            !SymGetTypeInfo(
                process,
                moduleBase,
                resolvedType,
                TI_GET_BASETYPE,
                &baseType)) {
            return false;
        }
        const std::uint64_t value = ReadUnsigned(
            snapshot + baseOffset + childOffset,
            length);
        std::wcout
            << childPath
            << L" @lastSession+0x"
            << std::hex << (baseOffset + childOffset)
            << std::dec << L" = ";
        if (resolvedTag == SymTagBaseType &&
            (baseType == PdbBaseTypeInt ||
             baseType == PdbBaseTypeLong)) {
            std::wcout << SignExtend(value, length);
        } else {
            std::wcout << value;
        }
        std::wcout << L"\n";
    }
    return true;
}

bool ReadStableSnapshot(
    HANDLE process,
    DWORD64 runtimeAddress,
    const Field& lockField,
    const Field& snapshotField,
    std::vector<std::uint8_t>* snapshot,
    unsigned* attempts) {
    if (lockField.length != sizeof(ULONG_PTR) ||
        snapshotField.length == 0 ||
        snapshotField.length > 64 * 1024) {
        SetLastError(ERROR_INVALID_DATA);
        return false;
    }
    snapshot->resize(static_cast<std::size_t>(snapshotField.length));
    std::vector<std::uint8_t> second(snapshot->size());

    for (unsigned attempt = 1; attempt <= 40; ++attempt) {
        ULONG_PTR before = 0;
        ULONG_PTR after = 0;
        SIZE_T read = 0;
        const auto lockAddress = reinterpret_cast<const void*>(
            runtimeAddress + lockField.offset);
        const auto snapshotAddress = reinterpret_cast<const void*>(
            runtimeAddress + snapshotField.offset);
        if (!ReadProcessMemory(
                process,
                lockAddress,
                &before,
                sizeof(before),
                &read) ||
            read != sizeof(before) ||
            before != 0 ||
            !ReadProcessMemory(
                process,
                snapshotAddress,
                snapshot->data(),
                snapshot->size(),
                &read) ||
            read != snapshot->size() ||
            !ReadProcessMemory(
                process,
                lockAddress,
                &after,
                sizeof(after),
                &read) ||
            read != sizeof(after) ||
            after != 0) {
            Sleep(1);
            continue;
        }
        MemoryBarrier();
        if (!ReadProcessMemory(
                process,
                snapshotAddress,
                second.data(),
                second.size(),
                &read) ||
            read != second.size() ||
            !ReadProcessMemory(
                process,
                lockAddress,
                &after,
                sizeof(after),
                &read) ||
            read != sizeof(after) ||
            after != 0 ||
            *snapshot != second) {
            Sleep(1);
            continue;
        }
        *attempts = attempt;
        return true;
    }
    SetLastError(ERROR_RETRY);
    return false;
}

} // namespace snapshot_inspector
