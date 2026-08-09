param(
    [ValidateRange(1, [int]::MaxValue)]
    [int] $ItemId = 16204,

    [ValidateNotNullOrEmpty()]
    [string] $ProcessName = 'Origin',

    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($processes.Count -ne 1) {
    throw "Expected one $ProcessName process, but found $($processes.Count)."
}

if (-not ('Reborn.Tools.OriginRuntimeInspector' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Reborn.Tools
{
    public sealed class OriginMountCandidate
    {
        public string ItemAddress { get; set; }
        public int ItemId { get; set; }
        public int Quality { get; set; }
        public int Grade { get; set; }
        public string TemplateAddress { get; set; }
        public int TemplateId { get; set; }
        public int TemplateSkillFlag { get; set; }
        public int HitValueCount { get; set; }
        public int[] HitValues { get; set; }
        public int? SelectedHit { get; set; }
        public int MaxHpValueCount { get; set; }
        public int[] MaxHpValues { get; set; }
        public int? SelectedMaxHp { get; set; }
        public int SpeedValueCount { get; set; }
        public float[] SpeedValues { get; set; }
        public float? SelectedSpeed { get; set; }
    }

    public static class OriginRuntimeInspector
    {
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint MemCommit = 0x1000;
        private const uint PageGuard = 0x0100;
        private const uint PageNoAccess = 0x0001;
        private const int ItemSize = 0xF8;
        private const int ItemIdOffset = 0x30;
        private const int ItemQualityOffset = 0x48;
        private const int ItemGradeOffset = 0x49;
        private const int ItemTemplateOffset = 0xF4;
        private const int TemplateIdOffset = 0x48;
        private const int TemplateSkillFlagOffset = 0x214;
        private const int TemplateHitVectorOffset = 0xCC;
        private const int TemplateMaxHpVectorOffset = 0xEC;
        private const int TemplateSpeedBeginOffset = 0x2D0;
        private const int TemplateSpeedEndOffset = 0x2D4;
        private const int TemplateHeaderSize = 0x2D8;
        private const int MaximumSpeedValues = 64;
        private const int ScanChunkSize = 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(
            IntPtr process,
            IntPtr address,
            out MemoryBasicInformation information,
            UIntPtr informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr address,
            byte[] buffer,
            UIntPtr size,
            out UIntPtr bytesRead);

        public static OriginMountCandidate[] Inspect(
            int processId,
            int itemId)
        {
            var process = OpenProcess(
                ProcessVmRead | ProcessQueryInformation,
                false,
                processId);
            if (process == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Scan(process, itemId).ToArray();
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static List<OriginMountCandidate> Scan(
            IntPtr process,
            int itemId)
        {
            var results = new List<OriginMountCandidate>();
            var visited = new HashSet<long>();
            var informationSize = (UIntPtr)Marshal.SizeOf(
                typeof(MemoryBasicInformation));
            long address = 0x10000;
            const long maximum32BitAddress = 0x7FFF0000;

            while (address < maximum32BitAddress)
            {
                MemoryBasicInformation information;
                var queried = VirtualQueryEx(
                    process,
                    new IntPtr(address),
                    out information,
                    informationSize);
                if (queried == UIntPtr.Zero)
                {
                    address += 0x1000;
                    continue;
                }

                var regionBase = information.BaseAddress.ToInt64();
                var regionSize = checked((long)information.RegionSize.ToUInt64());
                var nextAddress = regionBase + Math.Max(regionSize, 0x1000);
                if (information.State == MemCommit &&
                    IsReadable(information.Protect))
                {
                    ScanRegion(
                        process,
                        regionBase,
                        regionSize,
                        itemId,
                        visited,
                        results);
                }

                address = nextAddress > address
                    ? nextAddress
                    : address + 0x1000;
            }

            return results;
        }

        private static bool IsReadable(uint protection)
        {
            if ((protection & PageGuard) != 0 ||
                (protection & 0xFF) == PageNoAccess)
            {
                return false;
            }

            switch (protection & 0xFF)
            {
                case 0x02: // PAGE_READONLY
                case 0x04: // PAGE_READWRITE
                case 0x08: // PAGE_WRITECOPY
                case 0x20: // PAGE_EXECUTE_READ
                case 0x40: // PAGE_EXECUTE_READWRITE
                case 0x80: // PAGE_EXECUTE_WRITECOPY
                    return true;
                default:
                    return false;
            }
        }

        private static void ScanRegion(
            IntPtr process,
            long regionBase,
            long regionSize,
            int itemId,
            HashSet<long> visited,
            List<OriginMountCandidate> results)
        {
            var pattern = BitConverter.GetBytes(itemId);
            for (long offset = 0; offset < regionSize; offset += ScanChunkSize)
            {
                var count = (int)Math.Min(
                    ScanChunkSize + pattern.Length - 1L,
                    regionSize - offset);
                var buffer = Read(process, regionBase + offset, count);
                if (buffer == null)
                {
                    continue;
                }

                for (var index = 0;
                     index <= buffer.Length - pattern.Length;
                     index++)
                {
                    if (buffer[index] != pattern[0] ||
                        buffer[index + 1] != pattern[1] ||
                        buffer[index + 2] != pattern[2] ||
                        buffer[index + 3] != pattern[3])
                    {
                        continue;
                    }

                    var itemAddress = regionBase + offset + index -
                        ItemIdOffset;
                    if (itemAddress < 0x10000 || !visited.Add(itemAddress))
                    {
                        continue;
                    }

                    OriginMountCandidate candidate;
                    if (TryReadCandidate(
                        process,
                        itemAddress,
                        itemId,
                        out candidate))
                    {
                        results.Add(candidate);
                    }
                }
            }
        }

        private static bool TryReadCandidate(
            IntPtr process,
            long itemAddress,
            int expectedItemId,
            out OriginMountCandidate candidate)
        {
            candidate = null;
            var item = Read(process, itemAddress, ItemSize);
            if (item == null ||
                BitConverter.ToInt32(item, ItemIdOffset) != expectedItemId)
            {
                return false;
            }

            var quality = item[ItemQualityOffset];
            var templateAddress = BitConverter.ToUInt32(
                item,
                ItemTemplateOffset);
            if (quality > 25 || templateAddress < 0x10000)
            {
                return false;
            }

            var template = Read(
                process,
                templateAddress,
                TemplateHeaderSize);
            if (template == null)
            {
                return false;
            }

            var templateId = BitConverter.ToInt32(
                template,
                TemplateIdOffset);
            if (templateId != expectedItemId)
            {
                return false;
            }

            int[] hitValues;
            if (!TryReadIntVector(
                    process,
                    template,
                    TemplateHitVectorOffset,
                    out hitValues))
            {
                return false;
            }

            int[] maxHpValues;
            if (!TryReadIntVector(
                    process,
                    template,
                    TemplateMaxHpVectorOffset,
                    out maxHpValues))
            {
                return false;
            }

            var speedBegin = BitConverter.ToUInt32(
                template,
                TemplateSpeedBeginOffset);
            var speedEnd = BitConverter.ToUInt32(
                template,
                TemplateSpeedEndOffset);
            if (speedBegin > speedEnd ||
                (speedEnd - speedBegin) % sizeof(float) != 0)
            {
                return false;
            }

            var speedCount = checked((int)(
                (speedEnd - speedBegin) / sizeof(float)));
            if (speedCount > MaximumSpeedValues)
            {
                return false;
            }

            var speedBytes = speedCount == 0
                ? Array.Empty<byte>()
                : Read(process, speedBegin, speedCount * sizeof(float));
            if (speedBytes == null)
            {
                return false;
            }

            var speeds = new float[speedCount];
            for (var index = 0; index < speeds.Length; index++)
            {
                speeds[index] = BitConverter.ToSingle(
                    speedBytes,
                    index * sizeof(float));
            }

            candidate = new OriginMountCandidate
            {
                ItemAddress = "0x" + itemAddress.ToString("X8"),
                ItemId = expectedItemId,
                Quality = quality,
                Grade = item[ItemGradeOffset],
                TemplateAddress = "0x" + templateAddress.ToString("X8"),
                TemplateId = templateId,
                TemplateSkillFlag = BitConverter.ToInt32(
                    template,
                    TemplateSkillFlagOffset),
                HitValueCount = hitValues.Length,
                HitValues = hitValues,
                SelectedHit = quality >= 1 && quality <= hitValues.Length
                    ? (int?)hitValues[quality - 1]
                    : null,
                MaxHpValueCount = maxHpValues.Length,
                MaxHpValues = maxHpValues,
                SelectedMaxHp = quality >= 1 && quality <= maxHpValues.Length
                    ? (int?)maxHpValues[quality - 1]
                    : null,
                SpeedValueCount = speedCount,
                SpeedValues = speeds,
                SelectedSpeed = quality >= 1 && quality <= speedCount
                    ? (float?)speeds[quality - 1]
                    : null
            };
            return true;
        }

        private static bool TryReadIntVector(
            IntPtr process,
            byte[] template,
            int vectorOffset,
            out int[] values)
        {
            values = null;
            var begin = BitConverter.ToUInt32(template, vectorOffset + 4);
            var end = BitConverter.ToUInt32(template, vectorOffset + 8);
            if (begin > end || (end - begin) % sizeof(int) != 0)
            {
                return false;
            }

            var count = checked((int)((end - begin) / sizeof(int)));
            if (count > MaximumSpeedValues)
            {
                return false;
            }

            var bytes = count == 0
                ? Array.Empty<byte>()
                : Read(process, begin, count * sizeof(int));
            if (bytes == null)
            {
                return false;
            }

            values = new int[count];
            for (var index = 0; index < count; index++)
            {
                values[index] = BitConverter.ToInt32(
                    bytes,
                    index * sizeof(int));
            }

            return true;
        }

        private static byte[] Read(
            IntPtr process,
            long address,
            int count)
        {
            if (count < 0)
            {
                return null;
            }
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[count];
            UIntPtr bytesRead;
            if (!ReadProcessMemory(
                    process,
                    new IntPtr(address),
                    buffer,
                    (UIntPtr)count,
                    out bytesRead) ||
                bytesRead.ToUInt64() != (ulong)count)
            {
                return null;
            }

            return buffer;
        }
    }
}
'@
}

$candidates = @(
    [Reborn.Tools.OriginRuntimeInspector]::Inspect(
        $processes[0].Id,
        $ItemId))

$json = $candidates |
    Sort-Object ItemAddress |
    ConvertTo-Json -Depth 4

if ($OutputPath) {
    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($resolvedOutputPath)) | Out-Null
    [IO.File]::WriteAllText(
        $resolvedOutputPath,
        $json,
        [Text.UTF8Encoding]::new($false))
}
else {
    $json
}

if ($candidates.Count -eq 0) {
    Write-Warning (
        "No live item object for item ID $ItemId was found. " +
        'Enter the world and ensure the mount is equipped, then retry.')
}
