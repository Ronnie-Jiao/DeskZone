[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$IconPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resourceUpdaterType = 'DeskZoneInstallerIcon.ResourceUpdater'
if (-not ($resourceUpdaterType -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskZoneInstallerIcon
{
    internal sealed class IconResource
    {
        public byte[] GroupData { get; private set; }
        public List<byte[]> Images { get; private set; }

        public static IconResource Parse(byte[] ico)
        {
            if (ico == null || ico.Length < 6)
                throw new InvalidDataException("ICO header is incomplete.");

            ushort reserved = BitConverter.ToUInt16(ico, 0);
            ushort type = BitConverter.ToUInt16(ico, 2);
            ushort count = BitConverter.ToUInt16(ico, 4);
            if (reserved != 0 || type != 1 || count == 0)
                throw new InvalidDataException("ICO format is invalid.");

            int directoryLength = checked(6 + (count * 14));
            var group = new byte[directoryLength];
            Buffer.BlockCopy(ico, 0, group, 0, 6);
            var images = new List<byte[]>(count);

            for (int index = 0; index < count; index++)
            {
                int sourceOffset = 6 + (index * 16);
                if (sourceOffset + 16 > ico.Length)
                    throw new InvalidDataException("ICO image directory is incomplete.");

                uint imageLength = BitConverter.ToUInt32(ico, sourceOffset + 8);
                uint imageOffset = BitConverter.ToUInt32(ico, sourceOffset + 12);
                if (imageLength == 0 || imageOffset > ico.Length || imageLength > ico.Length - imageOffset)
                    throw new InvalidDataException("ICO image data range is invalid.");

                var image = new byte[checked((int)imageLength)];
                Buffer.BlockCopy(ico, checked((int)imageOffset), image, 0, checked((int)imageLength));
                images.Add(image);

                int groupOffset = 6 + (index * 14);
                Buffer.BlockCopy(ico, sourceOffset, group, groupOffset, 12);
                ushort resourceId = checked((ushort)(index + 1));
                group[groupOffset + 12] = (byte)(resourceId & 0xff);
                group[groupOffset + 13] = (byte)(resourceId >> 8);
            }

            return new IconResource { GroupData = group, Images = images };
        }
    }

    public static class ResourceUpdater
    {
        private const uint LoadLibraryAsDataFile = 0x00000002;

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate bool EnumResourceNamesProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceNamesProc callback, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(
            IntPtr update,
            IntPtr type,
            IntPtr name,
            ushort language,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] data,
            uint dataSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr update, bool discard);

        private static IntPtr ResourceId(ushort value)
        {
            return new IntPtr(value);
        }

        private static List<IntPtr> GetGroupIconNames(string fileName)
        {
            var result = new List<IntPtr>();
            IntPtr module = LoadLibraryEx(fileName, IntPtr.Zero, LoadLibraryAsDataFile);
            if (module == IntPtr.Zero)
                return result;

            try
            {
                var callback = new EnumResourceNamesProc((currentModule, type, name, parameter) =>
                {
                    if (name.ToInt64() <= 0xffff)
                        result.Add(name);
                    return true;
                });
                EnumResourceNames(module, ResourceId(14), callback, IntPtr.Zero);
            }
            finally
            {
                FreeLibrary(module);
            }

            return result;
        }

        public static void ReplaceIcon(string executablePath, byte[] ico)
        {
            IconResource resource = IconResource.Parse(ico);
            List<IntPtr> groupNames = GetGroupIconNames(executablePath);
            if (groupNames.Count == 0)
                groupNames.Add(ResourceId(1));

            IntPtr update = BeginUpdateResource(executablePath, false);
            if (update == IntPtr.Zero)
                throw new InvalidOperationException("Cannot open installer icon resources. Win32 error: " + Marshal.GetLastWin32Error());

            bool committed = false;
            try
            {
                for (ushort index = 0; index < resource.Images.Count; index++)
                {
                    if (!UpdateResource(update, ResourceId(3), ResourceId((ushort)(index + 1)), 0,
                        resource.Images[index], checked((uint)resource.Images[index].Length)))
                        throw new InvalidOperationException("Cannot write installer image resources. Win32 error: " + Marshal.GetLastWin32Error());
                }

                foreach (IntPtr groupName in groupNames)
                {
                    if (!UpdateResource(update, ResourceId(14), groupName, 0,
                        resource.GroupData, checked((uint)resource.GroupData.Length)))
                        throw new InvalidOperationException("Cannot write installer icon group resources. Win32 error: " + Marshal.GetLastWin32Error());
                }

                if (!EndUpdateResource(update, false))
                    throw new InvalidOperationException("Cannot commit installer icon resources. Win32 error: " + Marshal.GetLastWin32Error());
                committed = true;
            }
            finally
            {
                if (!committed)
                    EndUpdateResource(update, true);
            }
        }
    }
}
'@
}

$resolvedExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$resolvedIconPath = [IO.Path]::GetFullPath($IconPath)
if (-not (Test-Path -LiteralPath $resolvedExecutablePath -PathType Leaf)) {
    throw "找不到待处理的安装包：$resolvedExecutablePath"
}
if (-not (Test-Path -LiteralPath $resolvedIconPath -PathType Leaf)) {
    throw "找不到 DeskZone 图标：$resolvedIconPath"
}

[DeskZoneInstallerIcon.ResourceUpdater]::ReplaceIcon(
    $resolvedExecutablePath,
    [IO.File]::ReadAllBytes($resolvedIconPath))

Write-Host "已将 DeskZone 图标写入安装包：$resolvedExecutablePath"
