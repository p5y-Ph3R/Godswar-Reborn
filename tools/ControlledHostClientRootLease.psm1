Set-StrictMode -Version Latest

$moduleRoot = Split-Path -Parent $PSCommandPath
Import-Module (
    Join-Path $moduleRoot 'SecureNetworkPathSafety.psm1'
)

if (-not ('RebornControlledHostDirectoryLeaseNativeV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class RebornControlledHostDirectoryLeaseNativeV1
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileListDirectory = 0x00000001;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    public static SafeFileHandle Open(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path, FileReadAttributes | FileListDirectory,
            ShareRead | ShareWrite, IntPtr.Zero,
            OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "Cannot acquire the controlled-host client root lease.");
        }
        return handle;
    }

    public static string Identity(SafeFileHandle handle)
    {
        ByHandleFileInformation information;
        if (!GetFileInformationByHandle(handle, out information))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot read the controlled-host client root identity.");
        return information.VolumeSerialNumber.ToString("X8") + "-" +
            information.FileIndexHigh.ToString("X8") +
            information.FileIndexLow.ToString("X8");
    }
}
'@
}

function Enter-RebornControlledHostDirectoryLease {
    param([Parameter(Mandatory)][string]$DirectoryPath)

    $root = Assert-RebornDirectoryPath (
        [IO.Path]::GetFullPath($DirectoryPath).TrimEnd('\')
    ) 'controlled-host directory lease'
    $handle =
        [RebornControlledHostDirectoryLeaseNativeV1]::Open($root)
    try {
        return [pscustomobject]@{
            ClientRoot = $root
            Identity =
                [RebornControlledHostDirectoryLeaseNativeV1]::Identity(
                    $handle)
            Handle = $handle
        }
    }
    catch {
        $handle.Dispose()
        throw
    }
}

function Assert-RebornControlledHostDirectoryLease {
    param([Parameter(Mandatory)][object]$Lease)

    if ($Lease.Handle -isnot [Microsoft.Win32.SafeHandles.SafeFileHandle] -or
        $Lease.Handle.IsClosed -or $Lease.Handle.IsInvalid) {
        throw 'The controlled-host client root lease is not active.'
    }
    $current =
        [RebornControlledHostDirectoryLeaseNativeV1]::Open(
            [string]$Lease.ClientRoot)
    try {
        $currentIdentity =
            [RebornControlledHostDirectoryLeaseNativeV1]::Identity(
                $current)
        if ($currentIdentity -cne [string]$Lease.Identity -or
            [RebornControlledHostDirectoryLeaseNativeV1]::Identity(
                $Lease.Handle) -cne [string]$Lease.Identity) {
            throw 'The controlled-host client root identity changed.'
        }
    }
    finally {
        $current.Dispose()
    }
    return $true
}

function Exit-RebornControlledHostDirectoryLease {
    param([Parameter(Mandatory)][object]$Lease)

    if ($Lease.Handle -is
        [Microsoft.Win32.SafeHandles.SafeFileHandle]) {
        $Lease.Handle.Dispose()
    }
}

function Enter-RebornControlledHostClientRootLease {
    param([Parameter(Mandatory)][string]$ClientRoot)
    return Enter-RebornControlledHostDirectoryLease $ClientRoot
}

function Assert-RebornControlledHostClientRootLease {
    param([Parameter(Mandatory)][object]$Lease)
    return Assert-RebornControlledHostDirectoryLease $Lease
}

function Exit-RebornControlledHostClientRootLease {
    param([Parameter(Mandatory)][object]$Lease)
    Exit-RebornControlledHostDirectoryLease $Lease
}

Export-ModuleMember -Function @(
    'Enter-RebornControlledHostDirectoryLease',
    'Assert-RebornControlledHostDirectoryLease',
    'Exit-RebornControlledHostDirectoryLease',
    'Enter-RebornControlledHostClientRootLease',
    'Assert-RebornControlledHostClientRootLease',
    'Exit-RebornControlledHostClientRootLease'
)
