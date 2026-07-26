Set-StrictMode -Version Latest

if (-not ('RebornSecureFileHandleNativeV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class RebornSecureFileHandleNativeV1
{
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    public static uint GetLinkCount(SafeFileHandle handle)
    {
        if (handle == null || handle.IsInvalid)
            throw new ArgumentException("A valid file handle is required.");
        ByHandleFileInformation information;
        if (!GetFileInformationByHandle(handle, out information))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Cannot read link count from the exclusive file handle.");
        return information.NumberOfLinks;
    }
}
'@
}

function Assert-RebornSingleLinkFileStream {
    param(
        [Parameter(Mandatory)][IO.FileStream]$Stream,
        [Parameter(Mandatory)][string]$Label
    )

    if ([RebornSecureFileHandleNativeV1]::GetLinkCount(
            $Stream.SafeFileHandle) -ne 1) {
        throw "$Label cannot be a hard-linked file."
    }
}

Export-ModuleMember -Function 'Assert-RebornSingleLinkFileStream'
