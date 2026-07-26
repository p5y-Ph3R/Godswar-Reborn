Set-StrictMode -Version Latest

if (-not ('RebornSecurePathNativeV1' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class RebornSecurePathNativeV1
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareReadWriteDelete = 0x00000007;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        StringBuilder value,
        uint length,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    public static string GetFinalPath(string path)
    {
        using (SafeFileHandle handle = CreateFileW(
            path, FileReadAttributes, ShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, BackupSemantics, IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot open the path for final-path validation.");

            uint capacity = 512;
            while (capacity <= 32768)
            {
                StringBuilder value = new StringBuilder((int)capacity);
                uint length = GetFinalPathNameByHandleW(
                    handle, value, capacity, 0);
                if (length == 0)
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Cannot resolve the final filesystem path.");
                if (length < capacity)
                    return value.ToString();
                if (length >= 32768)
                    break;
                capacity = length + 1;
            }
        }
        throw new InvalidOperationException(
            "The final filesystem path exceeds the supported limit.");
    }

    public static uint GetLinkCount(string path)
    {
        using (SafeFileHandle handle = CreateFileW(
            path, FileReadAttributes, ShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot open file metadata for link validation.");
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot read file metadata for link validation.");
            return information.NumberOfLinks;
        }
    }
}
'@
}

function Resolve-RebornCanonicalLocalPath {
    param([string]$Path, [string]$Label)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label cannot be blank."
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($resolved)
    if ($root -notmatch '^[A-Za-z]:\\$') {
        throw "$Label must be on a local drive."
    }
    if ($resolved.Length -gt $root.Length) {
        $resolved = $resolved.TrimEnd('\')
    }
    return $resolved
}

function Resolve-RebornNonRootLocalPath {
    param([string]$Path, [string]$Label, [switch]$MustExist)

    $resolved = Resolve-RebornCanonicalLocalPath $Path $Label
    $root = [IO.Path]::GetPathRoot($resolved)
    if ($resolved.Equals(
            $root,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label cannot be a filesystem root."
    }
    if ($MustExist) {
        Assert-RebornDirectoryPath $resolved $Label | Out-Null
    }
    return $resolved
}

function Get-RebornPathComponents {
    param([string]$Path)

    $root = [IO.Path]::GetPathRoot($Path)
    $components = @($root)
    if ($Path.Length -eq $root.Length) {
        return $components
    }
    $current = $root
    foreach ($segment in $Path.Substring($root.Length).Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            throw "Path contains an empty component: $Path"
        }
        $current = Join-Path $current $segment
        $components += $current
    }
    return $components
}

function ConvertFrom-RebornNativeFinalPath {
    param([string]$Path)

    if ($Path.StartsWith(
            '\\?\UNC\',
            [StringComparison]::OrdinalIgnoreCase)) {
        return '\\' + $Path.Substring(8)
    }
    if ($Path.StartsWith(
            '\\?\',
            [StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring(4)
    }
    return $Path
}

function Assert-RebornPathComponent {
    param(
        [string]$Path,
        [string]$Label,
        [ValidateSet('Directory', 'File')]
        [string]$Kind
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label cannot contain a reparse point: $Path"
    }
    if ($Kind -eq 'Directory' -and -not $item.PSIsContainer) {
        throw "$Label is not a directory: $Path"
    }
    if ($Kind -eq 'File' -and $item.PSIsContainer) {
        throw "$Label is not a regular file: $Path"
    }
    $expected = Resolve-RebornCanonicalLocalPath $Path $Label
    $native = [RebornSecurePathNativeV1]::GetFinalPath($expected)
    $final = Resolve-RebornCanonicalLocalPath (
        ConvertFrom-RebornNativeFinalPath $native
    ) $Label
    if (-not $expected.Equals(
            $final,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label final path escapes its requested path: $Path"
    }
}

function Assert-RebornDirectoryPath {
    param([string]$Path, [string]$Label)

    $resolved = Resolve-RebornCanonicalLocalPath $Path $Label
    foreach ($component in Get-RebornPathComponents $resolved) {
        Assert-RebornPathComponent $component $Label Directory
    }
    return $resolved
}

function Assert-RebornRegularFilePath {
    param([string]$Path, [string]$Label)

    $resolved = Resolve-RebornCanonicalLocalPath $Path $Label
    Assert-RebornDirectoryPath (
        Split-Path -Parent $resolved
    ) "$Label parent" | Out-Null
    Assert-RebornPathComponent $resolved $Label File
    return $resolved
}

function Assert-RebornSingleLinkRegularFilePath {
    param([string]$Path, [string]$Label)

    $resolved = Assert-RebornRegularFilePath $Path $Label
    if ([RebornSecurePathNativeV1]::GetLinkCount($resolved) -ne 1) {
        throw "$Label cannot be a hard-linked file: $resolved"
    }
    return $resolved
}

function Test-RebornTrustedPathPrincipal {
    param([Security.Principal.IdentityReference]$Identity)

    $sid = $Identity.Translate(
        [Security.Principal.SecurityIdentifier]).Value
    return @(
        'S-1-5-18',
        'S-1-5-32-544',
        ('S-1-5-80-956008885-3418522649-1831038044-' +
            '1853292631-2271478464')
    ) -contains $sid
}

function Test-RebornDirectoryRuleHazard {
    param(
        [Security.AccessControl.FileSystemAccessRule]$Rule,
        [bool]$IsLeaf,
        [bool]$ProtectContents,
        [bool]$ProtectChildren
    )

    if ($Rule.AccessControlType -ne
            [Security.AccessControl.AccessControlType]::Allow -or
        (Test-RebornTrustedPathPrincipal $Rule.IdentityReference)) {
        return $false
    }
    $inheritOnly =
        ($Rule.PropagationFlags -band
            [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
    if ($inheritOnly -and
        -not ($IsLeaf -and ($ProtectContents -or $ProtectChildren))) {
        return $false
    }

    $hazards =
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    if (-not $IsLeaf -or $ProtectChildren) {
        $hazards = $hazards -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    }
    if ($IsLeaf -and $ProtectContents) {
        $hazards = $hazards -bor
            [Security.AccessControl.FileSystemRights]::WriteData -bor
            [Security.AccessControl.FileSystemRights]::AppendData -bor
            [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
            [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    }
    return ($Rule.FileSystemRights -band $hazards) -ne 0
}

function Assert-RebornProtectedDirectoryPath {
    param(
        [string]$Path,
        [string]$Label,
        [switch]$ProtectContents,
        [switch]$ProtectChildren,
        [switch]$RequireProtectedAcl
    )

    $resolved = Assert-RebornDirectoryPath $Path $Label
    $components = @(Get-RebornPathComponents $resolved)
    for ($index = 0; $index -lt $components.Count; $index++) {
        $component = $components[$index]
        $acl = Get-Acl -LiteralPath $component
        $owner = $acl.GetOwner(
            [Security.Principal.SecurityIdentifier])
        if (-not (Test-RebornTrustedPathPrincipal $owner)) {
            throw "$Label has an untrusted directory owner: $component"
        }
        $isLeaf = $index -eq ($components.Count - 1)
        if ($isLeaf -and
            $RequireProtectedAcl -and
            -not $acl.AreAccessRulesProtected) {
            throw "$Label must have inheritance disabled: $component"
        }
        $rules = $acl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier])
        foreach ($rule in $rules) {
            if (Test-RebornDirectoryRuleHazard `
                    $rule $isLeaf $ProtectContents $ProtectChildren) {
                throw (
                    "$Label is mutable by a nonprivileged principal at " +
                    "$component ($($rule.IdentityReference.Value))."
                )
            }
        }
    }
    return $resolved
}

function Assert-RebornProtectedRegularFilePath {
    param([string]$Path, [string]$Label)

    $resolved = Assert-RebornRegularFilePath $Path $Label
    Assert-RebornProtectedDirectoryPath `
        (Split-Path -Parent $resolved) "$Label parent" `
        -ProtectChildren | Out-Null
    $acl = Get-Acl -LiteralPath $resolved
    $owner = $acl.GetOwner(
        [Security.Principal.SecurityIdentifier])
    if (-not (Test-RebornTrustedPathPrincipal $owner)) {
        throw "$Label has an untrusted file owner: $resolved"
    }
    $hazards =
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $rules = $acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            ($rule.PropagationFlags -band
                [Security.AccessControl.PropagationFlags]::InheritOnly) -eq
                0 -and
            -not (Test-RebornTrustedPathPrincipal $rule.IdentityReference) -and
            ($rule.FileSystemRights -band $hazards) -ne 0) {
            throw (
                "$Label is writable by a nonprivileged principal " +
                "($($rule.IdentityReference.Value))."
            )
        }
    }
    return $resolved
}

function Assert-RebornProtectedFileSet {
    param([string[]]$Paths, [string]$Label)

    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path) {
            Assert-RebornProtectedRegularFilePath `
                $path $Label | Out-Null
        }
    }
}

function New-RebornProtectedDirectorySecurity {
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administrators =
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($system, $administrators)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function Initialize-RebornProtectedDirectoryPath {
    param([string]$Path, [string]$Label)

    $resolved = Resolve-RebornCanonicalLocalPath $Path $Label
    $parent = Split-Path -Parent $resolved
    Assert-RebornProtectedDirectoryPath `
        $parent "$Label parent" -ProtectChildren | Out-Null
    if (-not (Test-Path -LiteralPath $resolved)) {
        [IO.Directory]::CreateDirectory(
            $resolved,
            (New-RebornProtectedDirectorySecurity)) | Out-Null
    }
    Assert-RebornProtectedDirectoryPath `
        $resolved $Label -ProtectContents -RequireProtectedAcl
}

function Assert-RebornDirectChildDirectory {
    param(
        [string]$Path,
        [string]$Parent,
        [string]$Label,
        [switch]$RequireProtected
    )

    $resolvedParent =
        Resolve-RebornCanonicalLocalPath $Parent "$Label parent"
    $resolved = Resolve-RebornCanonicalLocalPath $Path $Label
    if (-not ([IO.Path]::GetDirectoryName($resolved)).Equals(
            $resolvedParent,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be a direct child of its configured parent."
    }
    if ($RequireProtected) {
        Assert-RebornProtectedDirectoryPath `
            $resolved $Label -ProtectContents -RequireProtectedAcl
    } else {
        Assert-RebornDirectoryPath $resolved $Label
    }
}

Export-ModuleMember -Function @(
    'Resolve-RebornCanonicalLocalPath',
    'Resolve-RebornNonRootLocalPath',
    'Assert-RebornDirectoryPath',
    'Assert-RebornRegularFilePath',
    'Assert-RebornSingleLinkRegularFilePath',
    'Assert-RebornProtectedDirectoryPath',
    'Assert-RebornProtectedRegularFilePath',
    'Assert-RebornProtectedFileSet',
    'Initialize-RebornProtectedDirectoryPath',
    'Assert-RebornDirectChildDirectory'
)
