using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace MarsForge.Launcher
{
    /// <summary>The app packed at the end of MarsForge.exe: [launcher exe][zip of the app][stamp 16][zip start 8]["MFPACK01"].</summary>
    sealed class Payload
    {
        public string File;
        public long Offset, Length;
        public Guid Stamp;
    }

    static class Package
    {
        const string Magic = "MFPACK01";
        const int TrailerSize = 32;
        const string AppExe = "MarsForge.exe", ReadyMark = ".ready";

        /// <summary>Finds the packed app in an exe, or null if there is none.</summary>
        public static Payload Find(string exe)
        {
            using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length < TrailerSize) return null;
                var t = new byte[TrailerSize];
                fs.Position = fs.Length - TrailerSize;
                if (fs.Read(t, 0, TrailerSize) != TrailerSize) return null;
                if (Encoding.ASCII.GetString(t, 24, 8) != Magic) return null;
                var g = new byte[16];
                Array.Copy(t, 0, g, 0, 16);
                long offset = BitConverter.ToInt64(t, 16);
                long length = fs.Length - TrailerSize - offset;
                if (offset <= 0 || length <= 0) return null;
                return new Payload { File = exe, Offset = offset, Length = length, Stamp = new Guid(g) };
            }
        }

        /// <summary>Build step: writes outExe = this launcher + a zip of appDir + the trailer.</summary>
        public static void Pack(string launcherExe, string appDir, string outExe)
        {
            appDir = Path.GetFullPath(appDir);
            outExe = Path.GetFullPath(outExe);
            if (!System.IO.File.Exists(Path.Combine(appDir, AppExe))) throw new FileNotFoundException("No " + AppExe + " in " + appDir);
            Directory.CreateDirectory(Path.GetDirectoryName(outExe));
            string zip = outExe + ".zip";
            if (System.IO.File.Exists(zip)) System.IO.File.Delete(zip);
            ZipFile.CreateFromDirectory(appDir, zip, CompressionLevel.Optimal, false);
            try
            {
                using (var output = new FileStream(outExe, FileMode.Create, FileAccess.Write))
                {
                    using (var launcher = new FileStream(launcherExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var self = Find(launcherExe);                      // never copy an old payload along
                        long n = self != null ? self.Offset : launcher.Length;
                        CopyBytes(launcher, output, n);
                    }
                    long offset = output.Position;
                    using (var z = System.IO.File.OpenRead(zip)) z.CopyTo(output, 1 << 20);
                    output.Write(Guid.NewGuid().ToByteArray(), 0, 16);
                    output.Write(BitConverter.GetBytes(offset), 0, 8);
                    output.Write(Encoding.ASCII.GetBytes(Magic), 0, 8);
                }
            }
            finally { System.IO.File.Delete(zip); }
        }

        static void CopyBytes(Stream from, Stream to, long count)
        {
            var buf = new byte[1 << 20];
            while (count > 0)
            {
                int n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) throw new EndOfStreamException();
                to.Write(buf, 0, n);
                count -= n;
            }
        }

        /// <summary>Folder for the unpacked app; a new build gets a new folder.</summary>
        public static string InstallDir(Payload p, string root) => Path.Combine(root, "app-" + p.Stamp.ToString("N").Substring(0, 12));

        public static string ExeIn(string dir) => Path.Combine(dir, AppExe);

        public static bool IsInstalled(string dir) =>
            System.IO.File.Exists(Path.Combine(dir, ReadyMark)) && System.IO.File.Exists(ExeIn(dir));

        /// <summary>Unpacks the app into dir. progress gets 0..1 (by bytes written).</summary>
        public static void Install(Payload p, string dir, Action<double> progress, CancellationToken ct)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);         // an earlier, unfinished try
            Directory.CreateDirectory(dir);
            string root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using (var fs = new FileStream(p.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var zip = new ZipArchive(new SubStream(fs, p.Offset, p.Length), ZipArchiveMode.Read))
            {
                long total = 0, done = 0;
                foreach (var e in zip.Entries) total += e.Length;
                var buf = new byte[1 << 20];
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = e.FullName.Replace('\\', '/');
                    string target = Path.GetFullPath(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // stay inside dir
                    if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = e.Open())
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            done += n;
                            progress(total > 0 ? (double)done / total : 1);
                            ct.ThrowIfCancellationRequested();
                        }
                    }
                }
            }
            System.IO.File.WriteAllText(Path.Combine(dir, ReadyMark), DateTime.Now.ToString("s"));
        }

        /// <summary>Deletes folders of older builds (skips any that are still in use).</summary>
        public static void RemoveOld(string root, string keep)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(root, "app-*"))
                    if (!string.Equals(Path.GetFullPath(d), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }
    }

    /// <summary>A read-only window into part of a file.</summary>
    sealed class SubStream : Stream
    {
        readonly Stream _s;
        readonly long _start, _length;
        long _pos;

        public SubStream(Stream s, long start, long length) { _s = s; _start = start; _length = length; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = Math.Max(0, Math.Min(_length, value)); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = _length - _pos;
            if (left <= 0) return 0;
            if (count > left) count = (int)left;
            _s.Position = _start + _pos;
            int n = _s.Read(buffer, offset, count);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _length + offset;
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
