# 从系统自带的「远程桌面连接」客户端提取图标，重打包为多尺寸 .ico。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File build/make-icon.ps1
#
# 为什么不是截屏或缩放：
#   mstsc.exe 的图标资源里已经有多档尺寸（16/32/48/256 等），
#   直接读取 RT_GROUP_ICON + RT_ICON 原始数据并重新拼成 .ico，
#   可以做到零缩放、零重采样，各档都是微软画的原图。
#   用 System.Drawing.Icon.ExtractAssociatedIcon 只能拿到一档，放大后必然发虚。
#
# 产物：src/DesktopTwin/Assets/DesktopTwin.ico
# 注意：图标版权归 Microsoft，随本项目一起分发属于「沿用系统视觉语言」，
#       商用或再分发前请自行确认授权。详见 THIRD-PARTY-NOTICES.md。

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repoRoot 'src/DesktopTwin/Assets/DesktopTwin.ico'

# 远程桌面客户端。用 System32 而不是 SysWOW64：32 位视图下可能拿到不同的资源。
$sourcePath = Join-Path $env:SystemRoot 'System32/mstsc.exe'
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "找不到系统远程桌面客户端：$sourcePath"
}

$source = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

public static class IconExtractor
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
    private const int RT_ICON = 3;
    private const int RT_GROUP_ICON = 14;

    private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNamesW(IntPtr hModule, IntPtr lpszType, EnumResNameProc callback, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpszName, IntPtr lpszType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    private static byte[] ReadResource(IntPtr hModule, IntPtr name, int type)
    {
        IntPtr info = FindResourceW(hModule, name, (IntPtr)type);
        if (info == IntPtr.Zero) return null;

        uint size = SizeofResource(hModule, info);
        if (size == 0) return null;

        IntPtr data = LoadResource(hModule, info);
        if (data == IntPtr.Zero) return null;

        IntPtr locked = LockResource(data);
        if (locked == IntPtr.Zero) return null;

        byte[] buffer = new byte[size];
        Marshal.Copy(locked, buffer, 0, (int)size);
        return buffer;
    }

    /// <summary>取第一个图标组，把它的全部尺寸拼成标准 .ico 字节流。</summary>
    public static byte[] Extract(string path)
    {
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == IntPtr.Zero)
            throw new InvalidOperationException("无法加载模块：" + path + "，错误 " + Marshal.GetLastWin32Error());

        try
        {
            var groupIds = new List<IntPtr>();
            EnumResNameProc collect = delegate(IntPtr h, IntPtr type, IntPtr name, IntPtr p)
            {
                groupIds.Add(name);
                return true;
            };
            EnumResourceNamesW(module, (IntPtr)RT_GROUP_ICON, collect, IntPtr.Zero);

            if (groupIds.Count == 0)
                throw new InvalidOperationException("该模块没有图标组资源。");

            // 资源 ID 是数值时低位就是 ID，直接按数值升序取第一个（通常是主图标）。
            groupIds.Sort(delegate(IntPtr a, IntPtr b) { return a.ToInt64().CompareTo(b.ToInt64()); });

            foreach (IntPtr groupId in groupIds)
            {
                byte[] group = ReadResource(module, groupId, RT_GROUP_ICON);
                if (group == null || group.Length < 6) continue;

                int count = BitConverter.ToUInt16(group, 4);
                if (count <= 0) continue;

                var images = new List<byte[]>(count);
                var entries = new List<int[]>(count);

                for (int i = 0; i < count; i++)
                {
                    int offset = 6 + i * 14;
                    if (offset + 14 > group.Length) break;

                    int width = group[offset];
                    int height = group[offset + 1];
                    int colorCount = group[offset + 2];
                    int planes = BitConverter.ToUInt16(group, offset + 4);
                    int bitCount = BitConverter.ToUInt16(group, offset + 6);
                    int id = BitConverter.ToUInt16(group, offset + 12);

                    byte[] image = ReadResource(module, (IntPtr)id, RT_ICON);
                    if (image == null) continue;

                    images.Add(image);
                    entries.Add(new[] { width, height, colorCount, planes, bitCount });
                }

                if (images.Count > 0)
                    return BuildIco(entries, images);
            }

            throw new InvalidOperationException("未能从任何图标组中读出图像数据。");
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static byte[] BuildIco(List<int[]> entries, List<byte[]> images)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);                 // reserved
            writer.Write((ushort)1);                 // type = icon
            writer.Write((ushort)images.Count);      // count

            int offset = 6 + images.Count * 16;
            for (int i = 0; i < images.Count; i++)
            {
                int[] meta = entries[i];
                writer.Write((byte)meta[0]);         // width（0 表示 256）
                writer.Write((byte)meta[1]);         // height
                writer.Write((byte)meta[2]);         // 调色板颜色数
                writer.Write((byte)0);               // reserved
                writer.Write((ushort)meta[3]);       // planes
                writer.Write((ushort)meta[4]);       // bit count
                writer.Write(images[i].Length);      // 数据长度
                writer.Write(offset);                // 数据偏移
                offset += images[i].Length;
            }

            foreach (byte[] image in images)
                writer.Write(image);

            writer.Flush();
            return stream.ToArray();
        }
    }
}
'@

Add-Type -TypeDefinition $source -Language CSharp

$bytes = [IconExtractor]::Extract($sourcePath)

$outputDirectory = Split-Path -Parent $outputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
[System.IO.File]::WriteAllBytes($outputPath, $bytes)

# 报告实际包含的尺寸，便于确认拿到的是多档原图而不是单一 32×32
$count = [BitConverter]::ToUInt16($bytes, 4)
Write-Host "已生成：$outputPath"
Write-Host ("包含 {0} 档尺寸：" -f $count)
for ($i = 0; $i -lt $count; $i++) {
    $offset = 6 + $i * 16
    $width = $bytes[$offset]
    $height = $bytes[$offset + 1]
    $bits = [BitConverter]::ToUInt16($bytes, $offset + 6)
    $size = [BitConverter]::ToUInt32($bytes, $offset + 8)
    if ($width -eq 0) { $width = 256 }
    if ($height -eq 0) { $height = 256 }
    Write-Host ("  {0}×{1}  {2}bpp  {3:N0} 字节" -f $width, $height, $bits, $size)
}
Write-Host ("文件大小：{0:N0} 字节" -f $bytes.Length)
