// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// The release bundle (ngc-host/scripts/make-bundle.py) and the native-only NuGet package packed from
/// it (ngc-host/scripts/pack.sh). The libraries here are small stand-in files that carry the runtime
/// pin the way the real ones do; the scripts do not run them.
/// </summary>
public class NativePackageTests
{
    const string Version = "9.8.7-test";
    const string Commit = "0123456789abcdef0123456789abcdef01234567";

    static readonly string RepoRoot = FindRepoRoot();
    static readonly string Pin = File.ReadAllText(Path.Combine(RepoRoot, "ngc-host", "LL-RUNTIME-PIN")).Trim();
    readonly string work = Path.Combine(Path.GetTempPath(), "slua-pkg-" + Guid.NewGuid().ToString("N"));

    public NativePackageTests() => Directory.CreateDirectory(work);

    // Found from this source file's own path, so it works when the build output is outside the repository.
    static string FindRepoRoot([CallerFilePath] string here = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(here)!); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ngc-host", "CMakeLists.txt"))) return d.FullName;
        throw new InvalidOperationException("repository root not found from " + here);
    }

    static (int Code, string Output) Run(string exe, string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o + err.Result);
    }

    static string Python() => OperatingSystem.IsWindows() ? "python" : "python3";

    // One folder as the CI build job uploads it: the library, the notices, the licences, the header.
    string BuildOutput(string name, string libName, string libText)
    {
        string d = Path.Combine(work, name);
        Directory.CreateDirectory(Path.Combine(d, "licenses"));
        File.WriteAllText(Path.Combine(d, libName), libText);
        File.Copy(Path.Combine(RepoRoot, "ngc-host", "THIRD-PARTY-NOTICES.txt"), Path.Combine(d, "THIRD-PARTY-NOTICES.txt"));
        File.Copy(Path.Combine(RepoRoot, "ngc-host", "shim", "slua_shim.h"), Path.Combine(d, "slua_shim.h"));
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot, "ngc-host", "licenses")))
            File.Copy(f, Path.Combine(d, "licenses", Path.GetFileName(f)));
        return d;
    }

    string MakeBundle(string winText = "stand-in win library PIN", string linuxText = "stand-in linux library PIN")
    {
        string win = BuildOutput("win", "slua_shim.dll", winText.Replace("PIN", Pin));
        string linux = BuildOutput("linux", "libslua_shim.so", linuxText.Replace("PIN", Pin));
        string bundle = Path.Combine(work, "bundle");
        var (code, o) = Run(Python(), RepoRoot, "-I", Path.Combine(RepoRoot, "ngc-host", "scripts", "make-bundle.py"),
            RepoRoot, Version, Commit, win, linux, bundle);
        Assert.True(code == 0, "make-bundle.py failed: " + o);
        return bundle;
    }

    static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void C1_TheBundleHasTheListedFilesAndTheChecksumsHold()
    {
        string bundle = MakeBundle();

        var expected = new SortedSet<string>(StringComparer.Ordinal)
        {
            $"slua-native-win-x64-{Version}.zip", $"slua-native-linux-x64-{Version}.tar.gz", "slua_shim.h",
            "THIRD-PARTY-NOTICES.txt", "RELEASE-NOTES.md", "SHA256SUMS.txt",
        };
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot, "ngc-host", "licenses")))
            expected.Add("licenses/" + Path.GetFileName(f));
        var actual = Directory.GetFiles(bundle, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(bundle, f).Replace('\\', '/')).ToList();
        Assert.Equal(expected, new SortedSet<string>(actual, StringComparer.Ordinal));

        // Every file but the checksum list itself is listed with its SHA-256.
        var listed = File.ReadAllLines(Path.Combine(bundle, "SHA256SUMS.txt"))
            .Where(l => l.Length > 0).Select(l => l.Split("  ", 2)).ToDictionary(p => p[1].TrimStart('*'), p => p[0]);
        Assert.Equal(expected.Where(f => f != "SHA256SUMS.txt").OrderBy(f => f, StringComparer.Ordinal), listed.Keys.OrderBy(f => f, StringComparer.Ordinal));
        foreach (var (name, hash) in listed)
            Assert.Equal(Sha256(Path.Combine(bundle, name)), hash);

        // The archives hold the one library each.
        using (var zip = ZipFile.OpenRead(Path.Combine(bundle, $"slua-native-win-x64-{Version}.zip")))
            Assert.Equal(new[] { "slua_shim.dll" }, zip.Entries.Select(e => e.FullName).ToArray());

        // The notes state the pin, the API version, the stamp format, the build's commit and the version.
        string notes = File.ReadAllText(Path.Combine(bundle, "RELEASE-NOTES.md"));
        Assert.Contains(Pin, notes);
        Assert.Contains("API version: 3", notes);
        Assert.Contains("runtime=<pin> api=<n>", notes);
        Assert.Contains(Commit, notes);
        Assert.Contains(Version, notes);
    }

    // The bundle as the release page holds it: every file a bundle contains is one asset, all in one
    // flat folder (a release has no folders), and a plain download is checked with sha256sum -c.
    [Fact]
    public void C2_TheChecksumsHoldOverTheFilesAsTheyAreDownloadedFromTheReleasePage()
    {
        string bundle = MakeBundle();
        string flat = Path.Combine(work, "release-page");
        Directory.CreateDirectory(flat);
        foreach (var f in Directory.GetFiles(bundle, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(flat, Path.GetFileName(f));
            Assert.False(File.Exists(dest), "two bundle files would have the same name on the release page: " + Path.GetFileName(f));
            File.Copy(f, dest);
        }

        var (code, o) = Run("sha256sum", flat, "-c", "SHA256SUMS.txt");
        Assert.True(code == 0, "sha256sum -c over the flat folder failed: " + o);

        // Nothing is listed twice or left unlisted: the list names exactly the other files.
        var listed = File.ReadAllLines(Path.Combine(flat, "SHA256SUMS.txt")).Where(l => l.Length > 0)
            .Select(l => l.Split("  ", 2)[1].TrimStart('*')).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var present = Directory.GetFiles(flat).Select(Path.GetFileName).Where(n => n != "SHA256SUMS.txt")
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(present, listed);
    }

    [Fact]
    public void C1_TheBundleIsRefusedWhenALibraryDoesNotCarryThePin()
    {
        string win = BuildOutput("win", "slua_shim.dll", "stand-in win library " + Pin);
        string linux = BuildOutput("linux", "libslua_shim.so", "stand-in linux library 0000000000");
        string bundle = Path.Combine(work, "bundle");
        var (code, o) = Run(Python(), RepoRoot, "-I", Path.Combine(RepoRoot, "ngc-host", "scripts", "make-bundle.py"),
            RepoRoot, Version, Commit, win, linux, bundle);
        Assert.NotEqual(0, code);
        Assert.Contains("libslua_shim.so", o);
        Assert.Contains("LL-RUNTIME-PIN", o);
        Assert.False(File.Exists(Path.Combine(bundle, "SHA256SUMS.txt")));
    }

    string Pack(string bundle, out string output)
    {
        string outDir = Path.Combine(work, "nupkg");
        var (code, o) = Run("sh", RepoRoot, Path.Combine(RepoRoot, "ngc-host", "scripts", "pack.sh"), RepoRoot, bundle, outDir);
        output = o;
        return code == 0 ? outDir : "";
    }

    [Fact]
    public void C1_TheNativePackageHasNoManagedAssemblyBothLibrariesAndTheNotices()
    {
        string bundle = MakeBundle();
        string outDir = Pack(bundle, out string o);
        Assert.True(outDir != "", "pack.sh failed: " + o);

        string pkg = Directory.GetFiles(outDir, "*.nupkg").Single();
        Assert.Equal($"UtopiaSkye.SLua.{Version}.nupkg", Path.GetFileName(pkg));
        using var zip = ZipFile.OpenRead(pkg);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        // Native only: no managed assembly or executable anywhere, no lib/ or ref/ folder.
        Assert.DoesNotContain(names, n => n.StartsWith("lib/") || n.StartsWith("ref/"));
        Assert.DoesNotContain(names, n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "runtimes/win-x64/native/slua_shim.dll" },
            names.Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray());
        Assert.DoesNotContain(names, n => n.Contains("SluaHost", StringComparison.OrdinalIgnoreCase));

        // Both native libraries are the bundle's, byte for byte, and the notices are there.
        Assert.Contains("runtimes/linux-x64/native/libslua_shim.so", names);
        Assert.Equal(File.ReadAllText(Path.Combine(work, "win", "slua_shim.dll")), ReadEntry(zip, "runtimes/win-x64/native/slua_shim.dll"));
        Assert.Equal(File.ReadAllText(Path.Combine(work, "linux", "libslua_shim.so")), ReadEntry(zip, "runtimes/linux-x64/native/libslua_shim.so"));
        Assert.Contains("THIRD-PARTY-NOTICES.txt", names);
        Assert.Contains("LICENSE.txt", names);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot, "ngc-host", "licenses")))
            Assert.Contains("licenses/" + Path.GetFileName(f), names);

        // Nothing the package depends on.
        string nuspec = ReadEntry(zip, "UtopiaSkye.SLua.nuspec");
        Assert.Contains($"<version>{Version}</version>", nuspec);
        Assert.DoesNotContain("<dependency ", nuspec);
    }

    [Fact]
    public void C1_PackingIsRefusedWhenAFileDoesNotMatchTheChecksums()
    {
        string bundle = MakeBundle();
        string archive = Path.Combine(bundle, $"slua-native-linux-x64-{Version}.tar.gz");
        var bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(archive, bytes);

        string outDir = Pack(bundle, out string o);
        Assert.Equal("", outDir);
        Assert.Contains("SHA256SUMS.txt", o);
        Assert.False(Directory.Exists(Path.Combine(work, "nupkg")) && Directory.GetFiles(Path.Combine(work, "nupkg"), "*.nupkg").Length > 0);
    }

    static string ReadEntry(ZipArchive zip, string name)
    {
        using var r = new StreamReader((zip.GetEntry(name) ?? throw new Xunit.Sdk.XunitException("missing " + name)).Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }
}
