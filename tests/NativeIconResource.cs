using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace OrgExplorer.TestFixtures
{
    public static class NativeIconResource
    {
        public static byte[] ReadExecutableIcon(string path)
        {
            return ReadExecutableIcon(path, 0);
        }

        public static byte[] ReadExecutableIcon(string path, int iconIndex)
        {
            // Data-file/resource mapping never runs application code or creates a window.
            var module = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 0x22);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
            try
            {
                byte[] group = Array.Empty<byte>();
                var resourceIndex = 0;
                EnumResourceName callback = (handle, type, name, parameter) =>
                {
                    if (iconIndex >= 0 ? resourceIndex++ != iconIndex : name.ToInt64() != -(long)iconIndex) return true;
                    group = ReadResource(handle, type, name);
                    return false;
                };
                EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero);
                GC.KeepAlive(callback);
                if (group.Length == 0) throw new InvalidDataException("Executable has no default native icon group: " + path);
                using (var reader = new BinaryReader(new MemoryStream(group, false)))
                using (var output = new MemoryStream())
                using (var writer = new BinaryWriter(output))
                {
                    if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
                        throw new InvalidDataException("Invalid native icon group.");
                    var count = reader.ReadUInt16();
                    if (count == 0 || group.Length != 6 + 14 * count)
                        throw new InvalidDataException("Truncated native icon group.");
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write(count);
                    var frames = new List<byte[]>();
                    var offset = 6 + 16 * count;
                    for (var index = 0; index < count; index++)
                    {
                        var description = reader.ReadBytes(8);
                        var length = reader.ReadUInt32();
                        var frame = ReadResource(module, new IntPtr(3), new IntPtr(reader.ReadUInt16()));
                        if (length != frame.Length) throw new InvalidDataException("Truncated native icon frame.");
                        writer.Write(description);
                        writer.Write(length);
                        writer.Write((uint)offset);
                        frames.Add(frame);
                        offset = checked(offset + frame.Length);
                    }
                    foreach (var frame in frames) writer.Write(frame);
                    writer.Flush();
                    return output.ToArray();
                }
            }
            finally { FreeLibrary(module); }
        }

        private static byte[] ReadResource(IntPtr module, IntPtr type, IntPtr name)
        {
            var resource = FindResource(module, name, type);
            if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var length = SizeofResource(module, resource);
            var pointer = LockResource(LoadResource(module, resource));
            if (length == 0 || length > 16 * 1024 * 1024 || pointer == IntPtr.Zero)
                throw new InvalidDataException("Invalid native icon resource.");
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }

        private delegate bool EnumResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResourceName callback, IntPtr parameter);
        [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
