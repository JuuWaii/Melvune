using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private const string Magic = "FNMUSIC-PAYLOAD-1"; // 17 bytes, followed by SHA256 and Int64 length.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate portable executable.");
            using var source = File.OpenRead(executable);
            source.Seek(-57, SeekOrigin.End);
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(17)) != Magic) throw new IOException("Portable payload is missing.");
            var expectedHash = reader.ReadBytes(32);
            var length = reader.ReadInt64();
            if (length <= 0 || length > source.Length - 57) throw new IOException("Invalid payload length.");
            var parent = Path.GetDirectoryName(executable)!;
            var root = Path.Combine(parent, "Melvune-portable");
            var legacyRoot = Path.Combine(parent, "FnMusic-portable");
            // Reuse existing portable Data without copying or overwriting credentials.
            if (!Directory.Exists(root) && Directory.Exists(legacyRoot)) root = legacyRoot;
            var extractionOnly = args.Length == 1 && args[0].StartsWith("--extract-only=", StringComparison.Ordinal);
            if (args.Length != 0 && !extractionOnly) throw new ArgumentException("Usage: double-click, or --extract-only=<empty absolute directory>.");
            var destination = extractionOnly ? args[0][15..] : Path.Combine(root, "App-" + Convert.ToHexString(expectedHash)[..12]);
            if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Extraction directory must be absolute.");
            destination = Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var extractionLock = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var marker = destination + ".sha256";
            if (extractionOnly && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("Extraction target must be empty.");
            if (!Directory.Exists(destination) || !File.Exists(marker) || File.ReadAllText(marker) != Convert.ToHexString(expectedHash))
            {
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                    throw new IOException("Incomplete portable application directory. Choose a new empty extraction directory.");
                var archivePath = destination + ".zip.partial";
                var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    source.Position = source.Length - 57 - length;
                    using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[1024 * 1024];
                        for (long remaining = length; remaining > 0;)
                        {
                            var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (count == 0) throw new EndOfStreamException();
                            archive.Write(buffer, 0, count);
                            remaining -= count;
                        }
                    }
                    using (var archive = File.OpenRead(archivePath))
                        if (!SHA256.HashData(archive).SequenceEqual(expectedHash)) throw new IOException("Portable payload checksum failed.");
                    Directory.CreateDirectory(staging);
                    using (var archive = ZipFile.OpenRead(archivePath))
                    {
                        var prefix = archive.Entries[0].FullName.Split('/')[0] + "/";
                        foreach (var entry in archive.Entries)
                        {
                            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Unexpected archive root.");
                            var relative = entry.FullName[prefix.Length..];
                            if (relative.Length == 0) continue;
                            var target = Path.GetFullPath(Path.Combine(staging, relative));
                            if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("Unsafe archive entry.");
                            if (entry.Name.Length == 0) Directory.CreateDirectory(target);
                            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, overwrite: false); }
                        }
                    }
                    if (Directory.Exists(destination)) Directory.Delete(destination); // Empty directory only.
                    Directory.Move(staging, destination);
                    File.WriteAllText(marker, Convert.ToHexString(expectedHash));
                }
                finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
            }
            if (extractionOnly) return 0;
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            var application = Path.Combine(destination, "Melvune.App.exe");
            if (!File.Exists(application)) application = Path.Combine(destination, "FnMusic.App.exe");
            var start = new ProcessStartInfo(application) { WorkingDirectory = destination, UseShellExecute = false };
            start.ArgumentList.Add("--data-directory=" + Path.Combine(root, "Data"));
            _ = Process.Start(start) ?? throw new IOException("Application could not start.");
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0) Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            if (args.Length == 0) MessageBoxW(0, "Melvune 便携版无法启动。请确认 EXE 所在目录可写且有足够空间。\n\n" + error.Message, "Melvune", 0x10);
            return 1;
        }
    }
}
