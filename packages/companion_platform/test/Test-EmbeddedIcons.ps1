#requires -Version 7.0
<#
.SYNOPSIS
只读验证 Windows 程序嵌入的每一帧图标与统一 ICO 源完全一致。
.DESCRIPTION
仅以数据文件及图像资源方式映射目标 EXE，不启动进程、不执行模块入口，
不解析导入依赖、不修改目标文件。每个模块无论验证成功还是失败均释放映射。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$ExecutablePath,
    [Parameter(Mandatory = $true)]
    [string]$IconPath,
    [int]$GroupIconId = 101
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw '该验证脚本仅支持 Windows。' }

# C# 封装只读资源 API；明确使用数据映射标志，避免把验证误写为执行 EXE。
if (-not ('MystiaIconEvidence.ResourceReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MystiaIconEvidence
{
    public sealed class FrameEvidence
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int ResourceId { get; set; }
        public int ByteCount { get; set; }
        public string Sha256 { get; set; }
    }

    public static class ResourceReader
    {
        // 数据映射不会调用 DllMain 或程序入口，也不会触发依赖模块加载。
        private const uint LoadAsDataFile = 0x00000002;
        private const uint LoadAsImageResource = 0x00000020;
        private const int GroupIconType = 14;
        private const int IconType = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resource);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        /// <summary>逐帧核验组目录、图像长度和图像原始字节，避免仅比较外观或首帧。</summary>
        public static FrameEvidence[] Verify(string executablePath, string iconPath, int groupIconId)
        {
            byte[] source = File.ReadAllBytes(iconPath);
            if (source.Length < 6 || BitConverter.ToUInt16(source, 0) != 0 ||
                BitConverter.ToUInt16(source, 2) != 1)
                throw new InvalidDataException("ICO 源文件头无效。");
            int count = BitConverter.ToUInt16(source, 4);
            if (count == 0 || source.Length < 6 + count * 16)
                throw new InvalidDataException("ICO 源文件目录为空或截断。");

            IntPtr module = LoadLibraryExW(executablePath, IntPtr.Zero,
                LoadAsDataFile | LoadAsImageResource);
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法只读映射目标程序。");
            try
            {
                byte[] group = ReadResource(module, GroupIconType, groupIconId);
                if (group.Length != 6 + count * 14)
                    throw new InvalidDataException("程序图标帧数或组目录长度与 ICO 源不一致。");
                for (int index = 0; index < 6; index++)
                    if (group[index] != source[index])
                        throw new InvalidDataException("程序图标组文件头与 ICO 源不一致。");

                var result = new FrameEvidence[count];
                for (int index = 0; index < count; index++)
                {
                    int sourceEntry = 6 + index * 16;
                    int groupEntry = 6 + index * 14;
                    // 两类目录前十二字节含尺寸、色数、位深与图像长度，布局相同。
                    for (int field = 0; field < 12; field++)
                        if (group[groupEntry + field] != source[sourceEntry + field])
                            throw new InvalidDataException("程序图标第 " + index + " 帧的目录属性不一致。");
                    uint length = BitConverter.ToUInt32(source, sourceEntry + 8);
                    uint offset = BitConverter.ToUInt32(source, sourceEntry + 12);
                    if ((ulong)offset + length > (ulong)source.Length || length > int.MaxValue)
                        throw new InvalidDataException("ICO 源图像范围超出文件边界。");
                    int resourceId = BitConverter.ToUInt16(group, groupEntry + 12);
                    byte[] image = ReadResource(module, IconType, resourceId);
                    if (image.Length != length)
                        throw new InvalidDataException("程序图标第 " + index + " 帧的实际长度不一致。");
                    for (int pixelByte = 0; pixelByte < image.Length; pixelByte++)
                        if (image[pixelByte] != source[(int)offset + pixelByte])
                            throw new InvalidDataException("程序图标第 " + index + " 帧的图像字节不一致。");
                    using (var sha = SHA256.Create())
                    {
                        result[index] = new FrameEvidence
                        {
                            Width = source[sourceEntry] == 0 ? 256 : source[sourceEntry],
                            Height = source[sourceEntry + 1] == 0 ? 256 : source[sourceEntry + 1],
                            ResourceId = resourceId,
                            ByteCount = image.Length,
                            Sha256 = BitConverter.ToString(sha.ComputeHash(image)).Replace("-", "").ToLowerInvariant()
                        };
                    }
                }
                return result;
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        /// <summary>复制资源字节后再释放模块，避免向调用者暴露依赖映射生命周期的指针。</summary>
        private static byte[] ReadResource(IntPtr module, int type, int id)
        {
            IntPtr resource = FindResourceW(module, new IntPtr(id), new IntPtr(type));
            if (resource == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "未找到图标资源 " + type + "/" + id);
            uint length = SizeofResource(module, resource);
            if (length == 0 || length > int.MaxValue)
                throw new InvalidDataException("图标资源长度无效。");
            IntPtr loaded = LoadResource(module, resource);
            if (loaded == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载图标资源。");
            IntPtr data = LockResource(loaded);
            if (data == IntPtr.Zero)
                throw new InvalidDataException("无法读取图标资源数据。");
            var bytes = new byte[(int)length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
'@
}

$resolvedIcon = (Resolve-Path -LiteralPath $IconPath).ProviderPath
$results = foreach ($candidate in $ExecutablePath) {
    $resolvedExecutable = (Resolve-Path -LiteralPath $candidate).ProviderPath
    $frames = [MystiaIconEvidence.ResourceReader]::Verify($resolvedExecutable, $resolvedIcon, $GroupIconId)
    [ordered]@{
        ExecutablePath = $resolvedExecutable
        ExecutableSha256 = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        IconPath = $resolvedIcon
        IconSha256 = (Get-FileHash -LiteralPath $resolvedIcon -Algorithm SHA256).Hash.ToLowerInvariant()
        FrameCount = $frames.Count
        Frames = @($frames)
        AllBytesMatch = $true
        TargetExecuted = $false
    }
}
@($results) | ConvertTo-Json -Depth 6
