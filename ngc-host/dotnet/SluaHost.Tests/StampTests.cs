// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// The saved-state stamp is "runtime=&lt;pin&gt; api=&lt;n&gt;" plus the bound-function hash. The
/// runtime part comes from ngc-host/LL-RUNTIME-PIN, checked against the parent tree at configure
/// time by ngc-host/stamp.cmake, and never from the repository's own HEAD. These tests run
/// stamp.cmake against small throwaway git repositories.
/// </summary>
public class StampTests
{
    static readonly string RepoRoot = FindRepoRoot();
    readonly string work = Path.Combine(Path.GetTempPath(), "slua-stamp-" + Guid.NewGuid().ToString("N"));

    public StampTests() => Directory.CreateDirectory(work);

    // The throwaway repositories are left in the system temp folder: git's object files are read-only,
    // and a CI runner's temp folder is discarded with the runner.
    // Found from this source file's own path (recorded at compile time), so it works when the
    // build output is kept outside the repository.
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

    string Git(string repo, params string[] args)
    {
        var all = new List<string> { "-c", "user.name=Test User", "-c", "user.email=test@example.org", "-c", "commit.gpgsign=false" };
        all.AddRange(args);
        var (code, o) = Run("git", repo, all.ToArray());
        Assert.True(code == 0, "git " + string.Join(' ', args) + " failed: " + o);
        return o.Trim();
    }

    string NewRepo()
    {
        string repo = Path.Combine(work, "repo" + Directory.GetDirectories(work).Length);
        Directory.CreateDirectory(Path.Combine(repo, "VM"));
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "VM", "a.cpp"), "int a;\n");
        File.WriteAllText(Path.Combine(repo, "CMakeLists.txt"), "project(x)\n");
        Commit(repo, "parent tree");
        return repo;
    }

    string Commit(string repo, string message)
    {
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", message);
        return Git(repo, "rev-parse", "--short=10", "HEAD");
    }

    void Write(string repo, string relative, string text)
    {
        string path = Path.Combine(repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Runs stamp.cmake; returns the exit code, the runtime text it wrote (empty if none) and its output.</summary>
    (int Code, string Runtime, string Output) Stamp(string repo, string pin, bool allowTree = false)
    {
        string pinFile = Path.Combine(work, "pin-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(pinFile, pin + "\n");
        string outFile = Path.Combine(work, "out-" + Guid.NewGuid().ToString("N"));
        var args = new List<string> { "-DSLUA_SOURCE_DIR=" + repo.Replace('\\', '/'), "-DSLUA_PIN_FILE=" + pinFile.Replace('\\', '/'), "-DSLUA_STAMP_OUT=" + outFile.Replace('\\', '/') };
        if (allowTree) args.Add("-DSLUA_ALLOW_UNPINNED_TREE=ON");
        args.Add("-P");
        args.Add(Path.Combine(RepoRoot, "ngc-host", "stamp.cmake"));
        var (code, o) = Run(Environment.GetEnvironmentVariable("SLUA_CMAKE") ?? "cmake", work, args.ToArray());
        return (code, File.Exists(outFile) ? File.ReadAllText(outFile).Trim() : "", o);
    }

    [Fact]
    public void A1_TwoConfiguresFromDifferentHeadsGiveTheSameStamp()
    {
        string repo = NewRepo();
        string pin = Git(repo, "rev-parse", "--short=10", "HEAD");
        // Commits that only touch the host's own files (docs, the workflow, the pin file).
        Write(repo, "ngc-host/README.md", "first\n");
        Write(repo, ".github/workflows/ngc-host.yml", "name: x\n");
        string head1 = Commit(repo, "host docs");
        var first = Stamp(repo, pin);
        Write(repo, "ngc-host/README.md", "second\n");
        Write(repo, ".github/workflows/ngc-host.yml", "name: y\n");
        string head2 = Commit(repo, "host docs again");
        var second = Stamp(repo, pin);

        Assert.NotEqual(head1, head2);
        Assert.True(first.Code == 0, first.Output);
        Assert.True(second.Code == 0, second.Output);
        Assert.Equal(pin, first.Runtime);
        Assert.Equal(first.Runtime, second.Runtime);
    }

    [Fact]
    public void A1_ADifferentPinGivesADifferentStamp()
    {
        string repo = NewRepo();
        string pinA = Git(repo, "rev-parse", "--short=10", "HEAD");
        var a = Stamp(repo, pinA);
        Write(repo, "VM/a.cpp", "int b;\n");
        string pinB = Commit(repo, "parent change");
        var b = Stamp(repo, pinB);

        Assert.True(a.Code == 0, a.Output);
        Assert.True(b.Code == 0, b.Output);
        Assert.NotEqual(pinA, pinB);
        Assert.NotEqual(a.Runtime, b.Runtime);
    }

    [Fact]
    public void A1_ParentFilesThatDifferFromThePinFailTheConfigure()
    {
        string repo = NewRepo();
        string pin = Git(repo, "rev-parse", "--short=10", "HEAD");
        Write(repo, "VM/a.cpp", "int changed;\n");
        Commit(repo, "parent change after the pin");
        var r = Stamp(repo, pin);

        Assert.NotEqual(0, r.Code);
        Assert.Equal("", r.Runtime);
        Assert.Contains("LL-RUNTIME-PIN", r.Output);
        Assert.Contains("SLUA_ALLOW_UNPINNED_TREE", r.Output);
    }

    [Fact]
    public void A1_TheOverrideStampsAContentHashOfTheParentTree()
    {
        string repo = NewRepo();
        string pin = Git(repo, "rev-parse", "--short=10", "HEAD");
        Write(repo, "VM/a.cpp", "int changed;\n");
        Commit(repo, "parent change after the pin");
        var one = Stamp(repo, pin, allowTree: true);
        Write(repo, "ngc-host/README.md", "host docs only\n");
        Commit(repo, "host docs");
        var sameTree = Stamp(repo, pin, allowTree: true);
        Write(repo, "VM/a.cpp", "int changed again;\n");
        Commit(repo, "another parent change");
        var otherTree = Stamp(repo, pin, allowTree: true);

        Assert.True(one.Code == 0, one.Output);
        Assert.StartsWith("tree-", one.Runtime);
        Assert.Equal(one.Runtime, sameTree.Runtime);
        Assert.NotEqual(one.Runtime, otherTree.Runtime);
    }

    [Fact]
    public void A1_TheBuiltLibraryCarriesThePinNotTheHead()
    {
        string pin = File.ReadAllText(Path.Combine(RepoRoot, "ngc-host", "LL-RUNTIME-PIN")).Trim();
        Assert.StartsWith("runtime=" + pin + " ", Host.Version());
    }

    [Fact]
    public void A1_TheApiVersionIsPartOfTheSavedStamp()
    {
        using var vm = new Vm(Watchdog.Resident);
        new TestKit().Register(vm);
        using var s = vm.NewScript(Host.Compile("while true do ossl.Nop() end", false), tag: 1);
        s.Start();
        Assert.Equal(StepStatus.Preempted, s.Step(0.01).Status);
        byte[] state = s.Serialize();

        // Header: "SLSH", u32 format, u16 stamp length, the stamp text ("runtime=... api=N"), u64 function hash.
        int len = BitConverter.ToUInt16(state, 8);
        string stamp = Encoding.ASCII.GetString(state, 10, len);
        int at = stamp.IndexOf("api=4", StringComparison.Ordinal);
        Assert.True(at >= 0, "stamp text was: " + stamp);
        state[10 + at + 4] = (byte)'2';

        using var vm2 = new Vm(Watchdog.Resident);
        new TestKit().Register(vm2);
        using var s2 = vm2.NewScript(Host.Compile("while true do ossl.Nop() end", false), tag: 1);
        Assert.False(s2.Restore(state));
    }
}
