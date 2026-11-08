using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace OrgExplorer.Workflow;

public sealed record PersonPhoto(string ContentType, int Width, int Height, ReadOnlyMemory<byte> Bytes);

public sealed partial class ApplicationWorkflow
{
    private Dictionary<string, byte[]> photos = [];
    private sealed record PhotoResource(string Directory, string RelativePath, string Field, string Hash,
        byte[] Bytes, string Extension);

    public PersonPhoto? GetPhoto(string email)
    { lock (workspaceGate) return GetPhotoCore(email); }

    private PersonPhoto? GetPhotoCore(string email)
    {
        if (!people.TryGetValue(email, out var person) || person.PhotoPath is null) return null;
        var bytes = photos[person.PhotoPath];
        return DecodePhoto(bytes.ToArray(), $"{email}.photoPath");
    }

    public bool EditPhoto(string email, string? localPath)
    { lock (workspaceGate) return EditPhotoCore(email, localPath); }

    private bool EditPhotoCore(string email, string? localPath)
    {
        if (workspace is null || !people.TryGetValue(email, out var person)) return Fail("Open a Person first.");
        try
        {
            var proposedPhotos = new Dictionary<string, byte[]>(photos);
            string? name = null;
            if (localPath is not null)
            {
                if (!Path.IsPathFullyQualified(localPath) || localPath.StartsWith(@"\\"))
                    throw new FormatException($"{email}.photoPath: choose a local JPEG/PNG file, not a remote path or URL.");
                var fullPath = Path.GetFullPath(localPath);
                var resource = ReadPhotoResource(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath), $"{email}.photoPath");
                name = resource.Hash.ToLowerInvariant() + resource.Extension;
                proposedPhotos[name] = resource.Bytes;
            }
            var proposed = new Dictionary<string, PersonProfile>(people, StringComparer.OrdinalIgnoreCase)
            { [email] = person with { PhotoPath = name } };
            var history = CaptureLocalHistory(person, proposed[email], "photoPath");
            if (!Save(proposed, batches, proposedPhotos, history, $"photo:{email.ToLowerInvariant()}")) return false;
            people = proposed;
            photos = proposedPhotos;
            profileHistory = history;
            pending = null;
            Status = "Saved locally.";
            return true;
        }
        catch (Exception ex) when (StorageError(ex))
        {
            return Fail("Photo edit rejected; existing photo retained: " + ex.Message);
        }
    }

    private static Dictionary<string, byte[]> LoadPhotos(string directory, Dictionary<string, PersonProfile> loaded)
    {
        var loadedPhotos = new Dictionary<string, byte[]>();
        foreach (var person in loaded.Values.Where(p => p.PhotoPath is not null))
        {
            var name = person.PhotoPath!;
            if (!Regex.IsMatch(name, @"^[a-f0-9]{64}\.(png|jpg)$"))
                throw new FormatException($"{person.Email}.photoPath: invalid Workspace image reference.");
            var resource = ReadPhotoResource(directory, Path.Combine("photos", name), $"{person.Email}.photoPath");
            loadedPhotos[name] = resource.Bytes;
        }
        return loadedPhotos;
    }

    private static void SavePhotos(string generation, Dictionary<string, PersonProfile> proposed,
        Dictionary<string, byte[]> proposedPhotos)
    {
        var names = proposed.Values.Select(p => p.PhotoPath).OfType<string>().Distinct().ToArray();
        if (names.Length == 0) return;
        var directory = Path.Combine(generation, "photos");
        Directory.CreateDirectory(directory);
        foreach (var name in names)
        {
            using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(proposedPhotos[name]);
            file.Flush(true);
        }
    }

    private static bool PhotoResourcesUnchanged(IReadOnlyList<PhotoResource> resources) =>
        resources.All(resource => ReadPhotoResource(resource.Directory, resource.RelativePath, resource.Field).Hash == resource.Hash);

    private static PhotoResource ReadPhotoResource(string directory, string relativePath, string field)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('\\') || relativePath.StartsWith('/') || relativePath.Contains(':') ||
            relativePath.Split(['\\', '/']).Any(part => part == ".."))
            throw new FormatException($"{field}: expected a safe relative path beneath the import directory; absolute, remote, and traversal paths are forbidden.");
        try
        {
            if (!OperatingSystem.IsWindows()) throw new IOException("Local photo decoding requires Windows.");
            var directoryPath = Path.GetFullPath(directory);
            var nativePath = directoryPath.StartsWith(@"\\?\") ? directoryPath :
                directoryPath.StartsWith(@"\\") ? @"\\?\UNC\" + directoryPath[2..] : @"\\?\" + directoryPath;
            using var root = CreateFile(nativePath,
                0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (root.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var resolvedRoot = FinalPath(root).TrimEnd('\\') + "\\";
            using var file = OpenWorkspaceRead(Path.Combine(directory, relativePath));
            // Validate the opened handle, not a lexical path checked before a link can change.
            if (!FinalPath(file.SafeFileHandle).StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"{field}: filesystem link escapes the containing import directory.");
            using var content = new MemoryStream();
            file.CopyTo(content);
            var bytes = content.ToArray();
            var photo = DecodePhoto(bytes, field);
            return new PhotoResource(directory, relativePath, field, Convert.ToHexString(SHA256.HashData(bytes)),
                bytes, photo.ContentType == "image/png" ? ".png" : ".jpg");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new FormatException($"{field}: supplied photo could not be read or decoded: {ex.Message}", ex);
        }
    }

    private static PersonPhoto DecodePhoto(byte[] bytes, string field)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Local photo decoding requires Windows.");
        var png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 });
        if (!png && !jpeg) throw new FormatException($"{field}: supplied photo must contain a decodable JPEG or PNG.");
        if (png) ValidatePng(bytes, field);
        else ValidateJpeg(bytes, field);
        try
        {
            var (width, height) = WindowsImageDecoder.Decode(bytes, png);
            return new PersonPhoto(png ? "image/png" : "image/jpeg", width, height, bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or OverflowException)
        {
            throw new FormatException($"{field}: supplied JPEG/PNG is corrupt or cannot be decoded.", ex);
        }
    }

    private static void ValidatePng(ReadOnlySpan<byte> bytes, string field)
    {
        var offset = 8;
        var hasPixels = false;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12) break;
            var count = (int)length;
            var chunk = bytes.Slice(offset + 4, count + 4);
            var type = chunk[..4];
            if (offset == 8 && (!type.SequenceEqual("IHDR"u8) || count != 13)) break;
            uint crc = uint.MaxValue;
            foreach (var value in chunk)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + count, 4))) break;
            offset += count + 12;
            if (type.SequenceEqual("IDAT"u8)) hasPixels = true;
            if (type.SequenceEqual("IEND"u8))
            {
                if (hasPixels && count == 0 && offset == bytes.Length) return;
                break;
            }
        }
        throw new FormatException($"{field}: supplied PNG is corrupt or truncated (invalid chunks/checksums).");
    }

    private static void ValidateJpeg(ReadOnlySpan<byte> bytes, string field)
    {
        var offset = 2;
        var hasScan = false;
        while (offset < bytes.Length && bytes[offset++] == 0xff)
        {
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker == 0xd9)
            {
                if (hasScan && offset == bytes.Length) return;
                break;
            }
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7 || offset > bytes.Length - 2) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset) break;
            offset += length;
            if (marker != 0xda) continue;
            hasScan = true;
            while (offset < bytes.Length)
            {
                if (bytes[offset] != 0xff) { offset++; continue; }
                var start = offset++;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) break;
                if (bytes[offset] is 0 or >= 0xd0 and <= 0xd7) { offset++; continue; }
                offset = start;
                break;
            }
        }
        throw new FormatException($"{field}: supplied JPEG is corrupt or truncated (invalid segments/end marker).");
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            if (GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0) == 0)
                throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
