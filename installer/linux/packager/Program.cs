using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: matdol-linux-packager <publish dir> <repository root> <output dir>");
    return 2;
}

var pub = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
var outDir = Path.GetFullPath(args[2]);
var maintainer = Environment.GetEnvironmentVariable("MATDOL_MAINTAINER") is { Length: > 0 } m ? m : "matdol <matdol@users.noreply.github.com>";

var csproj = File.ReadAllText(Path.Combine(root, "matdol.csproj"));
var version = Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value.Trim();
if (version.Length == 0) { Console.Error.WriteLine("could not read <Version> from matdol.csproj"); return 1; }
foreach (var f in new[] { "matdol", "libSkiaSharp.so", "libHarfBuzzSharp.so" })
    if (!File.Exists(Path.Combine(pub, f))) { Console.Error.WriteLine($"{f} not found in publish folder: {pub}"); return 1; }
Directory.CreateDirectory(outDir);
Console.WriteLine($"[matdol] version {version}");

var mtime = DateTimeOffset.UtcNow;
if (long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), out var sde)) mtime = DateTimeOffset.FromUnixTimeSeconds(sde);

const UnixFileMode Dir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode Exe = Dir;
const UnixFileMode Reg = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

static string Lf(string s) => s.Replace("\r\n", "\n");

var appFiles = new List<(string rel, byte[] data, bool exe)>();
foreach (var f in Directory.EnumerateFiles(pub, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
{
    var rel = Path.GetRelativePath(pub, f).Replace('\\', '/');
    if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || rel == "matdol.png") continue;
    appFiles.Add((rel, File.ReadAllBytes(f), rel == "matdol"));
}
foreach (var legal in new[] { "LICENSE.txt", "Privacy.txt", "ThirdPartyNotices.txt" })
    appFiles.Add(("legal/" + legal, File.ReadAllBytes(Path.Combine(root, "Assets", "Legal", legal)), false));

int[] iconSizes = { 16, 24, 32, 48, 64, 96, 128, 256, 512 };
var icons = iconSizes.Select(s => ($"hicolor/{s}x{s}/apps/matdol.png", File.ReadAllBytes(Path.Combine(root, "Assets", "Icons", $"icon-{s}.png")))).ToList();

const string Mimes = "image/png;image/jpeg;image/bmp;image/webp;image/gif;image/tiff;image/x-dpx;image/x-portable-pixmap;image/x-portable-graymap;image/x-portable-anymap;image/heic;image/heif;image/avif;image/x-adobe-dng;image/x-canon-cr2;image/x-nikon-nef;video/x-yuv4mpeg;";
string DesktopEntry(string exec, string icon) =>
    "[Desktop Entry]\nType=Application\nName=matdol\nGenericName=Image Viewer\n" +
    "Comment=matdol image viewer — raw YUV/RGB, DPX, TIFF, camera raw and common image formats\n" +
    $"Exec={exec} %f\nIcon={icon}\nTerminal=false\nCategories=Graphics;Viewer;\nMimeType={Mimes}\nStartupWMClass=matdol\nKeywords=image;viewer;yuv;raw;dpx;tiff;\n";

static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

static byte[] Gzip(byte[] data)
{
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(data);
    return ms.ToArray();
}

static void WriteAr(string path, IEnumerable<(string name, byte[] data)> members, DateTimeOffset mtime)
{
    using var fs = File.Create(path);
    var ascii = Encoding.ASCII;
    fs.Write(ascii.GetBytes("!<arch>\n"));
    foreach (var (name, data) in members)
    {
        var header = name.PadRight(16) + mtime.ToUnixTimeSeconds().ToString().PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "100644".PadRight(8) + data.Length.ToString().PadRight(10) + "`\n";
        fs.Write(ascii.GetBytes(header));
        fs.Write(data);
        if (data.Length % 2 == 1) fs.WriteByte((byte)'\n');
    }
}

var deb = new TarBuilder(mtime, "./");
foreach (var (rel, data, exe) in appFiles) deb.File("opt/matdol/" + rel, data, exe ? Exe : Reg, Dir);
deb.Link("usr/bin/matdol", "/opt/matdol/matdol", Dir);
foreach (var (rel, data) in icons) deb.File("usr/share/icons/" + rel, data, Reg, Dir);
deb.File("usr/share/applications/matdol.desktop", Utf8(DesktopEntry("/opt/matdol/matdol", "matdol")), Reg, Dir);
var copyright = "matdol — copyright and license\n\nmatdol is distributed under the MIT License (below).\n" +
                "Third-party components and their licenses: /opt/matdol/legal/ThirdPartyNotices.txt\n\n" + Lf(File.ReadAllText(Path.Combine(root, "LICENSE")));
deb.File("usr/share/doc/matdol/copyright", Utf8(copyright), Reg, Dir);
var changelog = $"matdol ({version}) stable; urgency=low\n\n  * Release {version}.\n\n -- {maintainer}  {mtime.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture)}\n";
deb.File("usr/share/doc/matdol/changelog.gz", Gzip(Utf8(changelog)), Reg, Dir);

var control =
    $"Package: matdol\nVersion: {version}\nArchitecture: amd64\nMaintainer: {maintainer}\nInstalled-Size: {deb.InstalledKb}\n" +
    "Section: graphics\nPriority: optional\n" +
    "Depends: libc6 (>= 2.17), libgcc-s1 | libgcc1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66 | libicu63 | libicu60\n" +
    "Recommends: libheif1\nSuggests: libheif-plugin-libde265, libheif-plugin-dav1d\n" +
    "Description: image viewer for image and video developers\n" +
    " Image viewer for image/video developers: raw YUV/RGB sequences, Y4M,\n" +
    " DPX, TIFF, camera raw and common image formats, with pixel values,\n" +
    " side-by-side and difference views.\n .\n" +
    " HEIC/HEIF/AVIF files are decoded by the system libheif (optional).\n";
var md5 = new StringBuilder();
foreach (var (path, data) in deb.Files) md5.Append(Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant()).Append("  ").Append(path).Append('\n');
var ctl = new TarBuilder(mtime, "./");
ctl.File("control", Utf8(control), Reg, Dir);
ctl.File("md5sums", Utf8(md5.ToString()), Reg, Dir);

var debPath = Path.Combine(outDir, $"matdol_{version}_amd64.deb");
WriteAr(debPath, new[] { ("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), ("control.tar.gz", ctl.Build(true)), ("data.tar.gz", deb.Build(true)) }, mtime);
Console.WriteLine($"[matdol] {debPath}");
CopyLatest(debPath, "matdol_amd64.deb");

var name = $"matdol-{version}-linux-x64";
var tgz = new TarBuilder(mtime, name + "/");
foreach (var (rel, data, exe) in appFiles) tgz.File("app/" + rel, data, exe ? Exe : Reg, Dir);
foreach (var (rel, data) in icons) tgz.File("icons/" + rel, data, Reg, Dir);
tgz.File("install.sh", Utf8(Lf(File.ReadAllText(Path.Combine(root, "installer", "linux", "install.sh")))), Exe, Dir);
tgz.File("app/uninstall.sh", Utf8(Lf(File.ReadAllText(Path.Combine(root, "installer", "linux", "uninstall.sh")))), Exe, Dir);
tgz.File("app/matdol.desktop.in", Utf8(DesktopEntry("@APPDIR@/matdol", "@ICON@")), Reg, Dir);
tgz.File("README.txt", Utf8(
    $"matdol {version} for Linux (x86-64)\n\n" +
    "Install for your account only (no administrator rights needed):\n    ./install.sh\n\n" +
    "Installs to ~/.local/share/matdol and adds matdol to your application menu\n" +
    "(and a desktop shortcut). Uninstall: ~/.local/share/matdol/uninstall.sh\n" +
    "Options: ./install.sh --help\n\n" +
    "Requirements: glibc 2.17 or later, an X11 desktop session.\n" +
    "HEIC/HEIF/AVIF need the system libheif (optional).\n"), Reg, Dir);
var tgzPath = Path.Combine(outDir, name + ".tar.gz");
File.WriteAllBytes(tgzPath, tgz.Build(true));
Console.WriteLine($"[matdol] {tgzPath}");
CopyLatest(tgzPath, "matdol-linux-x64.tar.gz");
return 0;

void CopyLatest(string src, string latestName)
{
    var dst = Path.Combine(outDir, latestName);
    File.Copy(src, dst, true);
    Console.WriteLine($"[matdol] {dst}");
}

sealed class TarBuilder
{
    private readonly SortedDictionary<string, (TarEntryType type, UnixFileMode mode, byte[]? data, string? link)> _items = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _mtime;
    private readonly string _prefix;
    public TarBuilder(DateTimeOffset mtime, string prefix) { _mtime = mtime; _prefix = prefix; }

    private void Dirs(string path, UnixFileMode mode)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
        {
            var d = string.Join('/', parts.Take(i)) + "/";
            if (!_items.ContainsKey(d)) _items[d] = (TarEntryType.Directory, mode, null, null);
        }
    }
    public void Dir(string path, UnixFileMode mode) { Dirs(path + "/x", mode); }
    public void File(string path, byte[] data, UnixFileMode mode, UnixFileMode dirMode) { Dirs(path, dirMode); _items[path] = (TarEntryType.RegularFile, mode, data, null); }
    public void Link(string path, string target, UnixFileMode dirMode) { Dirs(path, dirMode); _items[path] = (TarEntryType.SymbolicLink, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute, null, target); }
    public IEnumerable<(string path, byte[] data)> Files => _items.Where(i => i.Value.type == TarEntryType.RegularFile).Select(i => (i.Key, i.Value.data!));
    public long InstalledKb => _items.Sum(i => i.Value.type == TarEntryType.RegularFile ? (i.Value.data!.Length + 1023) / 1024 : i.Value.type == TarEntryType.Directory ? 4 : 0);

    public byte[] Build(bool rootDirEntry)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tw = new TarWriter(gz, TarEntryFormat.Gnu, leaveOpen: false))
        {
            if (rootDirEntry) tw.WriteEntry(Entry(TarEntryType.Directory, _prefix, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, null, null));
            foreach (var (path, v) in _items) tw.WriteEntry(Entry(v.type, _prefix + path, v.mode, v.data, v.link));
        }
        return ms.ToArray();
    }

    private GnuTarEntry Entry(TarEntryType type, string name, UnixFileMode mode, byte[]? data, string? link)
    {
        var e = new GnuTarEntry(type, name) { Mode = mode, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = _mtime };
        if (link != null) e.LinkName = link;
        if (data != null) e.DataStream = new MemoryStream(data);
        return e;
    }
}
