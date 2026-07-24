$ErrorActionPreference = 'Stop'

if (-not ('RebornParityRestartManager' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public sealed class RebornParityFileUser
{
    public int ProcessId { get; set; }
    public long ProcessStartFileTimeUtc { get; set; }
    public string ApplicationName { get; set; }
    public int ApplicationType { get; set; }
    public uint TerminalSessionId { get; set; }
    public bool Restartable { get; set; }
}

public static class RebornParityRestartManager
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaximumProcesses = 256;
    private const int SessionKeyCapacity = 33;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        uint sessionFlags,
        StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        RmUniqueProcess[] applications,
        uint serviceCount,
        string[] serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RmProcessInfo[] affectedApplications,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder imageName,
        ref uint imageNameLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static string GetProcessImagePath(int processId)
    {
        IntPtr process = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "OpenProcess failed.");
        try
        {
            uint length = 32768;
            StringBuilder path = new StringBuilder((int)length);
            if (!QueryFullProcessImageName(
                    process,
                    0,
                    path,
                    ref length))
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "QueryFullProcessImageName failed.");
            return path.ToString();
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static RebornParityFileUser[] GetFileUsers(string fileName)
    {
        uint session;
        StringBuilder sessionKey =
            new StringBuilder(SessionKeyCapacity);
        int result = RmStartSession(
            out session,
            0,
            sessionKey);
        if (result != ErrorSuccess)
            throw new InvalidOperationException(
                "Restart Manager start failed: " + result);

        try
        {
            result = RmRegisterResources(
                session,
                1,
                new[] { fileName },
                0,
                null,
                0,
                null);
            if (result != ErrorSuccess)
                throw new InvalidOperationException(
                    "Restart Manager registration failed: " + result);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint needed;
                uint count = 0;
                uint reasons = 0;
                result = RmGetList(
                    session,
                    out needed,
                    ref count,
                    null,
                    ref reasons);
                if (result == ErrorSuccess)
                    return new RebornParityFileUser[0];
                if (result != ErrorMoreData)
                    throw new InvalidOperationException(
                        "Restart Manager list failed: " + result);
                if (needed > MaximumProcesses)
                    throw new InvalidOperationException(
                        "Restart Manager process limit exceeded.");

                RmProcessInfo[] infos = new RmProcessInfo[needed];
                count = needed;
                result = RmGetList(
                    session,
                    out needed,
                    ref count,
                    infos,
                    ref reasons);
                if (result == ErrorMoreData)
                    continue;
                if (result != ErrorSuccess)
                    throw new InvalidOperationException(
                        "Restart Manager list failed: " + result);

                RebornParityFileUser[] users =
                    new RebornParityFileUser[count];
                for (int index = 0; index < count; index++)
                {
                    long high = (long)(uint)
                        infos[index].Process.StartTime.dwHighDateTime;
                    long low = (uint)
                        infos[index].Process.StartTime.dwLowDateTime;
                    users[index] = new RebornParityFileUser
                    {
                        ProcessId = infos[index].Process.ProcessId,
                        ProcessStartFileTimeUtc = (high << 32) | low,
                        ApplicationName = infos[index].ApplicationName,
                        ApplicationType = infos[index].ApplicationType,
                        TerminalSessionId =
                            infos[index].TerminalSessionId,
                        Restartable = infos[index].Restartable
                    };
                }
                return users;
            }
            throw new InvalidOperationException(
                "Restart Manager file users changed repeatedly.");
        }
        finally
        {
            RmEndSession(session);
        }
    }
}
'@
}

function Get-ParityRestartManagerFileUsers {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Restart Manager evidence file does not exist: $fullPath"
    }
    return @(
        [RebornParityRestartManager]::GetFileUsers($fullPath) |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    resourcePath = $fullPath
                    processId = [int]$_.ProcessId
                    processStartFileTimeUtc =
                        [long]$_.ProcessStartFileTimeUtc
                    applicationName = [string]$_.ApplicationName
                    applicationType = [int]$_.ApplicationType
                    terminalSessionId = [uint32]$_.TerminalSessionId
                    restartable = [bool]$_.Restartable
                }
            }
    )
}

function Get-ParityOriginRuntimeEvidence {
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ClientRoot,
        [Parameter(Mandatory)]
        [ValidateSet('ShimParity', 'StockRollback', 'FinalReapply')]
        [string]$Stage
    )

    $errors = @()
    $root = [IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
    $startedFileTime = $Process.StartTime.ToUniversalTime().ToFileTimeUtc()
    if ($Process.HasExited) {
        throw 'Origin.exe exited before runtime evidence collection.'
    }
    function Get-MatchingFileUser {
        param([Parameter(Mandatory)][string]$Path)

        $matching = @(
            Get-ParityRestartManagerFileUsers $Path |
                Where-Object {
                    $_.processId -eq $Process.Id -and
                    $_.processStartFileTimeUtc -eq $startedFileTime -and
                    $_.applicationName -ieq 'Origin.exe'
                }
        )
        if ($matching.Count -ne 1) {
            throw (
                'Restart Manager did not find exactly one matching ' +
                "Origin.exe file user: $Path"
            )
        }
        return $matching[0]
    }

    $processPath = $null
    $pathSource = $null
    $pathLocker = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace([string]$Process.Path)) {
            $processPath = [IO.Path]::GetFullPath($Process.Path)
            $pathSource = 'ProcessApi'
        }
    }
    catch {
        $processPath = $null
    }
    if (-not $processPath) {
        try {
            $processPath = [IO.Path]::GetFullPath(
                [RebornParityRestartManager]::GetProcessImagePath(
                    $Process.Id))
            $pathSource = 'QueryFullProcessImageName'
        }
        catch {
            $processPath = $null
        }
    }
    if (-not $processPath) {
        $errors += (
            'Origin path evidence failed: Process.Path and ' +
            'QueryFullProcessImageName were unavailable.'
        )
    }

    $modules = @()
    try {
        foreach ($module in @($Process.Modules)) {
            if ($module.ModuleName -in @('Net.dll', 'NetLegacy.dll')) {
                $path = [IO.Path]::GetFullPath($module.FileName)
                $modules += [pscustomobject][ordered]@{
                    name = [string]$module.ModuleName
                    path = $path
                    baseAddress = '0x{0:X8}' -f
                        $module.BaseAddress.ToInt64()
                    memorySize = [int]$module.ModuleMemorySize
                    diskSha256 = Get-FileHash `
                        -LiteralPath $path -Algorithm SHA256 |
                        Select-Object -ExpandProperty Hash
                    evidenceSource = 'ProcessModules'
                    locker = $null
                }
            }
        }
    }
    catch {
        $modules = @()
    }

    if ($modules.Count -eq 0) {
        $requiredNames = if ($Stage -eq 'StockRollback') {
            @('Net.dll')
        } else {
            @('Net.dll', 'NetLegacy.dll')
        }
        foreach ($name in $requiredNames) {
            $path = Join-Path $root $name
            try {
                $locker = Get-MatchingFileUser $path
                $modules += [pscustomobject][ordered]@{
                    name = $name
                    path = $path
                    baseAddress = $null
                    memorySize = $null
                    diskSha256 = Get-FileHash `
                        -LiteralPath $path -Algorithm SHA256 |
                        Select-Object -ExpandProperty Hash
                    evidenceSource = 'RestartManagerFileUse'
                    locker = $locker
                }
            }
            catch {
                $errors += (
                    "$name file-use evidence failed: " +
                    $_.Exception.Message
                )
            }
        }
    }

    try {
        $Process.Refresh()
        if ($Process.HasExited -or
            $Process.StartTime.ToUniversalTime().ToFileTimeUtc() -ne
                $startedFileTime) {
            $errors += 'Origin.exe changed during runtime evidence collection.'
        }
    }
    catch {
        $errors += "Origin liveness recheck failed: $($_.Exception.Message)"
    }

    return [pscustomobject][ordered]@{
        processPath = $processPath
        pathEvidenceSource = $pathSource
        pathLocker = $pathLocker
        processStartFileTimeUtc = [long]$startedFileTime
        modules = $modules
        errors = $errors
    }
}

Export-ModuleMember -Function @(
    'Get-ParityOriginRuntimeEvidence',
    'Get-ParityRestartManagerFileUsers'
)
