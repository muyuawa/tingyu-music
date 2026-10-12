using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Muyu.Core;

public sealed record LibraryRoot(string Id, string Path, string Identity, bool Recursive = true, bool Watch = true);
public sealed record ScanIssue(string Path, string Kind, string Message);
public sealed record SourceFingerprint(string Identity, long Length, long Modified, string Sha256);

public static class LocalFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    public static (string Identity, long Modified) Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("无法读取文件身份");
        return ($"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}", (long)(((ulong)info.WriteHigh << 32) | info.WriteLow));
    }
    public static string DirectoryIdentity(string path)
    {
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("目录离线或无法访问");
        return Read(handle).Identity;
    }
    public static SourceFingerprint Capture(string path, CancellationToken cancellation = default, SourceFingerprint? cached = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        var identity = Read(stream.SafeFileHandle);
        if (cached != null && cached.Identity == identity.Identity && cached.Modified == identity.Modified && cached.Length == stream.Length) return cached;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        cancellation.ThrowIfCancellationRequested();
        return new(identity.Identity, stream.Length, identity.Modified, Convert.ToHexString(hash.GetHashAndReset()));
    }
}

public static class LibraryScope
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Contains(string root, string path, bool recursive = true)
    {
        root = Normalize(root); path = Normalize(path);
        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) return true;
        if (!recursive) return string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase);
        return path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static void ValidateDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)) throw new IOException("所选路径不是文件夹");
        if (HasLink(path)) throw new IOException("不扫描符号链接或目录联接，请选择实际文件夹");
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); entries.MoveNext();
    }
    public static bool HasLink(string path)
    {
        string? current = Normalize(path);
        while (current != null)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }
    public static IEnumerable<string> Enumerate(string root, bool recursive, CancellationToken cancellation, IList<ScanIssue> issues)
    {
        ValidateDirectory(root);
        var directories = new Stack<string>(); directories.Push(Normalize(root));
        while (directories.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested(); string directory = directories.Pop(); string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(directory, "directory", ex.Message)); continue; }
            foreach (var entry in entries)
            {
                cancellation.ThrowIfCancellationRequested(); FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(entry, "access", ex.Message)); continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { issues.Add(new(entry, "link", "已跳过符号链接或目录联接")); continue; }
                if (attributes.HasFlag(FileAttributes.Directory)) { if (recursive) directories.Push(entry); }
                else yield return Path.GetFullPath(entry);
            }
        }
    }
}
