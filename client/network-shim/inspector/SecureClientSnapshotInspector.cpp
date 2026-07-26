#include "SnapshotInspectorPdb.h"
#include "../src/FileSha256.h"

#include <Windows.h>
#define _NO_CVCONST_H
#include <DbgHelp.h>
#include <TlHelp32.h>

#include <cstdint>
#include <cwchar>
#include <iomanip>
#include <iostream>
#include <string>
#include <vector>

namespace {

static_assert(sizeof(void*) == 4, "Build this inspector for Win32.");

struct Handle final {
    HANDLE value = INVALID_HANDLE_VALUE;

    Handle() noexcept = default;
    explicit Handle(HANDLE candidate) noexcept : value(candidate) {}
    ~Handle() noexcept {
        if (value != nullptr && value != INVALID_HANDLE_VALUE) {
            CloseHandle(value);
        }
    }

    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

struct Options final {
    DWORD pid = 0;
    DWORD waitSeconds = 0;
    std::wstring imagePath;
    std::wstring pdbPath;
    std::wstring expectedSha256;
};

struct ModuleRecord final {
    DWORD64 base = 0;
    DWORD size = 0;
    std::wstring path;
};

std::wstring ErrorText(DWORD error) {
    wchar_t* message = nullptr;
    const DWORD length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER |
            FORMAT_MESSAGE_FROM_SYSTEM |
            FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr,
        error,
        0,
        reinterpret_cast<wchar_t*>(&message),
        0,
        nullptr);
    std::wstring result =
        length != 0 && message != nullptr ? message : L"unknown error";
    if (message != nullptr) {
        LocalFree(message);
    }
    while (!result.empty() &&
           (result.back() == L'\r' || result.back() == L'\n')) {
        result.pop_back();
    }
    return result;
}

bool ParseUnsigned(const wchar_t* text, DWORD* value) {
    if (text == nullptr || value == nullptr || *text == L'\0') {
        return false;
    }
    wchar_t* end = nullptr;
    const unsigned long candidate = std::wcstoul(text, &end, 10);
    if (end == text || *end != L'\0') {
        return false;
    }
    *value = static_cast<DWORD>(candidate);
    return true;
}

bool ParseOptions(int argc, wchar_t** argv, Options* options) {
    if (options == nullptr) {
        return false;
    }
    for (int index = 1; index < argc; index += 2) {
        if (index + 1 >= argc) {
            return false;
        }
        const std::wstring key = argv[index];
        const wchar_t* value = argv[index + 1];
        if (key == L"--pid") {
            if (!ParseUnsigned(value, &options->pid) ||
                options->pid == 0) {
                return false;
            }
        } else if (key == L"--image") {
            options->imagePath = value;
        } else if (key == L"--pdb") {
            options->pdbPath = value;
        } else if (key == L"--expected-sha256") {
            options->expectedSha256 = value;
        } else if (key == L"--wait-seconds") {
            if (!ParseUnsigned(value, &options->waitSeconds)) {
                return false;
            }
        } else {
            return false;
        }
    }
    return options->pid != 0 &&
        !options->imagePath.empty() &&
        !options->pdbPath.empty() &&
        options->expectedSha256.size() == 64;
}

bool ParseSha256(
    const std::wstring& text,
    std::uint8_t (&bytes)[32]) {
    auto nibble = [](wchar_t value) -> int {
        if (value >= L'0' && value <= L'9') {
            return value - L'0';
        }
        if (value >= L'a' && value <= L'f') {
            return value - L'a' + 10;
        }
        if (value >= L'A' && value <= L'F') {
            return value - L'A' + 10;
        }
        return -1;
    };
    if (text.size() != 64) {
        return false;
    }
    for (std::size_t index = 0; index < 32; ++index) {
        const int high = nibble(text[index * 2]);
        const int low = nibble(text[index * 2 + 1]);
        if (high < 0 || low < 0) {
            return false;
        }
        bytes[index] = static_cast<std::uint8_t>((high << 4) | low);
    }
    return true;
}

std::wstring DirectoryName(const std::wstring& path) {
    const std::size_t separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos
        ? L"."
        : path.substr(0, separator);
}

bool SamePath(const std::wstring& first, const std::wstring& second) {
    wchar_t firstFull[MAX_PATH]{};
    wchar_t secondFull[MAX_PATH]{};
    if (GetFullPathNameW(
            first.c_str(),
            static_cast<DWORD>(std::size(firstFull)),
            firstFull,
            nullptr) == 0 ||
        GetFullPathNameW(
            second.c_str(),
            static_cast<DWORD>(std::size(secondFull)),
            secondFull,
            nullptr) == 0) {
        return false;
    }
    return _wcsicmp(firstFull, secondFull) == 0;
}

bool FindModule(DWORD pid, const wchar_t* moduleName, ModuleRecord* module) {
    Handle snapshot(CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
        pid));
    if (snapshot.value == INVALID_HANDLE_VALUE) {
        return false;
    }

    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Module32FirstW(snapshot.value, &entry)) {
        return false;
    }
    do {
        if (_wcsicmp(entry.szModule, moduleName) == 0) {
            module->base =
                reinterpret_cast<DWORD64>(entry.modBaseAddr);
            module->size = entry.modBaseSize;
            module->path = entry.szExePath;
            return true;
        }
    } while (Module32NextW(snapshot.value, &entry));
    SetLastError(ERROR_MOD_NOT_FOUND);
    return false;
}

bool WaitForModule(
    DWORD pid,
    DWORD waitSeconds,
    ModuleRecord* module) {
    const ULONGLONG deadline =
        GetTickCount64() + static_cast<ULONGLONG>(waitSeconds) * 1000;
    while (true) {
        if (FindModule(pid, L"Net.dll", module)) {
            return true;
        }
        if (GetLastError() != ERROR_MOD_NOT_FOUND ||
            GetTickCount64() >= deadline) {
            return false;
        }
        Sleep(250);
    }
}

int Inspect(const Options& options) {
    std::uint8_t expectedHash[32]{};
    if (!ParseSha256(options.expectedSha256, expectedHash)) {
        std::wcerr << L"Invalid SHA-256 text.\n";
        return 2;
    }
    if (!godswar::network::FileMatchesSha256(
            options.imagePath.c_str(),
            expectedHash,
            sizeof(expectedHash))) {
        std::wcerr << L"Candidate image SHA-256 mismatch.\n";
        return 3;
    }

    ModuleRecord module;
    if (!WaitForModule(options.pid, options.waitSeconds, &module)) {
        std::wcerr
            << L"Net.dll not loaded in PID " << options.pid
            << L": " << ErrorText(GetLastError()) << L"\n";
        return 4;
    }
    if (!godswar::network::FileMatchesSha256(
            module.path.c_str(),
            expectedHash,
            sizeof(expectedHash))) {
        std::wcerr << L"Loaded Net.dll SHA-256 mismatch.\n";
        return 5;
    }

    Handle process(OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
        FALSE,
        options.pid));
    if (process.value == nullptr) {
        std::wcerr
            << L"OpenProcess failed: "
            << ErrorText(GetLastError()) << L"\n";
        return 6;
    }

    SymSetOptions(
        SYMOPT_CASE_INSENSITIVE |
        SYMOPT_DEFERRED_LOADS |
        SYMOPT_EXACT_SYMBOLS |
        SYMOPT_FAIL_CRITICAL_ERRORS |
        SYMOPT_NO_PROMPTS |
        SYMOPT_UNDNAME);
    const std::wstring searchPath =
        DirectoryName(options.pdbPath) + L";" +
        DirectoryName(options.imagePath);
    if (!SymInitializeW(process.value, searchPath.c_str(), FALSE)) {
        std::wcerr
            << L"SymInitialize failed: "
            << ErrorText(GetLastError()) << L"\n";
        return 7;
    }

    const DWORD64 loadedBase = SymLoadModuleExW(
        process.value,
        nullptr,
        options.imagePath.c_str(),
        L"Net",
        module.base,
        module.size,
        nullptr,
        0);
    if (loadedBase == 0) {
        std::wcerr
            << L"SymLoadModuleEx failed: "
            << ErrorText(GetLastError()) << L"\n";
        SymCleanup(process.value);
        return 8;
    }

    snapshot_inspector::RuntimeSymbol runtime;
    std::vector<snapshot_inspector::RuntimeSymbol> observations;
    std::size_t matchCount = 0;
    if (!snapshot_inspector::FindRuntimeSymbol(
            process.value,
            loadedBase,
            &runtime,
            &observations,
            &matchCount)) {
        std::wcerr
            << L"Expected one SecureClientRuntime data symbol; found "
            << matchCount << L".\n";
        for (const auto& observation : observations) {
            std::wcerr
                << L"  name=\"" << observation.name
                << L"\" tag=" << observation.tag
                << L" type=\"" << observation.typeName
                << L"\" size=" << observation.size
                << L" address=0x" << std::hex
                << observation.address << std::dec << L"\n";
        }
        SymCleanup(process.value);
        return 10;
    }

    IMAGEHLP_MODULEW64 moduleInfo{};
    moduleInfo.SizeOfStruct = sizeof(moduleInfo);
    if (!SymGetModuleInfoW64(
            process.value,
            loadedBase,
            &moduleInfo) ||
        moduleInfo.SymType != SymPdb ||
        !SamePath(moduleInfo.LoadedPdbName, options.pdbPath)) {
        std::wcerr
            << L"DbgHelp did not load the requested exact PDB. "
            << L"SymType=" << moduleInfo.SymType
            << L", loaded=\"" << moduleInfo.LoadedPdbName
            << L"\", requested=\"" << options.pdbPath << L"\".\n";
        SymCleanup(process.value);
        return 9;
    }

    snapshot_inspector::Field lockField;
    snapshot_inspector::Field snapshotField;
    if (!snapshot_inspector::FindMember(
            process.value,
            loadedBase,
            runtime.typeId,
            L"lastSessionLock_",
            &lockField) ||
        !snapshot_inspector::FindMember(
            process.value,
            loadedBase,
            runtime.typeId,
            L"lastSession_",
            &snapshotField)) {
        std::wcerr
            << L"Required PDB fields were not found: "
            << ErrorText(GetLastError()) << L"\n";
        SymCleanup(process.value);
        return 11;
    }

    std::vector<std::uint8_t> snapshot;
    unsigned attempts = 0;
    if (!snapshot_inspector::ReadStableSnapshot(
            process.value,
            runtime.address,
            lockField,
            snapshotField,
            &snapshot,
            &attempts)) {
        std::wcerr
            << L"Could not obtain a stable unlocked snapshot: "
            << ErrorText(GetLastError()) << L"\n";
        SymCleanup(process.value);
        return 12;
    }

    std::wcout
        << L"pid = " << options.pid << L"\n"
        << L"candidate_sha256 = " << options.expectedSha256 << L"\n"
        << L"loaded_module_path = " << module.path << L"\n"
        << L"module_base = 0x" << std::hex << module.base << L"\n"
        << L"module_size = 0x" << module.size << L"\n"
        << L"runtime_symbol = " << runtime.name << L"\n"
        << L"runtime_rva = 0x" << (runtime.address - module.base) << L"\n"
        << L"runtime_size = 0x" << runtime.size << L"\n"
        << L"lastSessionLock_offset = 0x" << lockField.offset << L"\n"
        << L"lastSession_offset = 0x" << snapshotField.offset << L"\n"
        << L"lastSession_size = 0x" << snapshotField.length << L"\n"
        << std::dec
        << L"stable_read_attempts = " << attempts << L"\n"
        << L"privacy = allowlisted snapshot scalars only; "
           L"no payload, key, nonce, token, proof, or raw buffer output\n";

    const bool dumped = snapshot_inspector::DumpSnapshotType(
        process.value,
        loadedBase,
        snapshotField.typeId,
        snapshot.data(),
        snapshot.size(),
        0,
        L"");
    const DWORD dumpError = dumped ? ERROR_SUCCESS : GetLastError();
    SymCleanup(process.value);
    if (!dumped) {
        std::wcerr
            << L"PDB snapshot traversal failed closed: "
            << ErrorText(dumpError) << L"\n";
        return 13;
    }
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    Options options;
    if (!ParseOptions(argc, argv, &options)) {
        std::wcerr
            << L"Usage: SecureClientSnapshotInspector.exe "
               L"--pid <pid> --image <Net.dll> --pdb <Net.pdb> "
               L"--expected-sha256 <64 hex> [--wait-seconds <seconds>]\n";
        return 2;
    }
    return Inspect(options);
}
