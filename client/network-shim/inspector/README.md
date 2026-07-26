# Secure client snapshot inspector

This Win32 console tool reads the retained
`SecureClientRuntime::LastSessionSnapshot` from a live 32-bit `Origin.exe`.
It is built separately from `Net.dll`; the inspector project has no project
reference to the shim and cannot rebuild or relink it.

The reader fails closed unless all of these checks pass:

- both the candidate and loaded `Net.dll` match the caller-supplied SHA-256;
- DbgHelp loads the exact caller-supplied PDB for the image;
- the PDB contains one function-local `SecureClientRuntime` named `runtime`;
- the PDB exposes `lastSessionLock_` and `lastSession_` with sane sizes;
- the target SRW lock is unlocked before and after two identical reads.

The process handle requests only `PROCESS_QUERY_INFORMATION |
PROCESS_VM_READ`. The tool does not suspend threads, acquire the target lock,
call target code, inject code, or request write/operation access.

Output is fail-closed and limited to scalar fields in an explicit allowlist of
snapshot structures. Arrays, pointers, unknown structures, payloads, keys,
nonces, tokens, proofs, and raw buffers are never printed.

## Build

From the repository root:

```powershell
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$install = & $vswhere -latest -products '*' -version '[17.0,18.0)' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
$msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild `
    client\network-shim\inspector\Godswar.NetShim.SnapshotInspector.vcxproj `
    /m /t:Rebuild /p:Configuration=Release /p:Platform=Win32 `
    /p:VCToolsVersion=14.44.35207 `
    /p:WindowsTargetPlatformVersion=10.0.26100.0 /v:minimal
```

## Inspect

```powershell
& client\network-shim\bin\Inspector\Win32\SecureClientSnapshotInspector.exe `
    --pid 63044 `
    --image client\network-shim\bin\Release\Win32\Net.dll `
    --pdb client\network-shim\bin\Release\Win32\Net.pdb `
    --expected-sha256 6A45CD25B19C33A827735854333303A5E3A62C06E070F03F71964668EEE3B8A0 `
    --wait-seconds 30
```

`--wait-seconds` only polls the read-only module list for `Net.dll`. Use zero
to fail immediately when the module has not loaded.
