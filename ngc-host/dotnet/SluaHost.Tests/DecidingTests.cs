// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Diagnostics;
using SluaHost;
using Xunit;

// The shim is exercised by timing-sensitive tests (watchdog, run windows), so
// test classes run one after another. The concurrency tests start their own threads.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SluaHost.Tests;

public class DecidingTests
{
    static (Vm vm, TestKit kit) NewVm(Watchdog w = Watchdog.Resident, bool extra = false)
    {
        var kit = new TestKit();
        var vm = new Vm(w);
        kit.Register(vm, extra);
        return (vm, kit);
    }

    static Script Run(Vm vm, string source, bool lsl = false, long tag = 1, int limit = 128 * 1024)
    {
        var s = vm.NewScript(Host.Compile(source, lsl), limit, tag);
        s.Start();
        return s;
    }

    // ---- Test 1: script calls .NET ----

    [Fact]
    public void T1_ScriptCallsHostAndGetsValuesBack()
    {
        var (vm, kit) = NewVm();
        using var _ = vm;
        using var s = Run(vm, """
            ll.OwnerSay("x", 3, vector(1, 2, 3))
            local grid = ossl.GetGridName()
            local sum = ossl.Add(2, 3)
            local a, b, c = ossl.Echo("e", true, vector(4, 5, 6))
            ossl.Report(grid, sum, a, b, c)
            """);
        var r = s.RunToIdle();
        Assert.Equal(StepStatus.Ok, r.Status);
        var lines = kit.Snapshot();
        Assert.Equal("OwnerSay str:x num:3 vec:<1,2,3>", lines[0]);
        Assert.Equal("str:Example Grid num:5 str:e true vec:<4,5,6>", lines[1]);
    }

    [Fact]
    public void T1_ThrowingHostCallbackBecomesScriptErrorAndProcessSurvives()
    {
        var (vm, kit) = NewVm();
        using var _ = vm;
        // Caught by the script itself.
        using (var s = Run(vm, """
            local ok, err = pcall(ossl.Boom)
            ossl.Report("pcall", ok, err)
            """))
        {
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            string line = kit.Snapshot().Single();
            Assert.StartsWith("str:pcall false", line);
            Assert.Contains("boom from host", line);
        }
        // Not caught: the script faults, the VM and the process carry on.
        using (var bad = Run(vm, "ossl.Boom()", tag: 2))
        {
            var r = bad.RunToIdle();
            Assert.Equal(StepStatus.Fault, r.Status);
            var (kind, msg) = bad.Fault();
            Assert.Equal(FaultKind.Runtime, kind);
            Assert.Contains("boom from host", msg);
        }
        using var good = Run(vm, "ossl.Report('still alive')", tag: 3);
        Assert.Equal(StepStatus.Ok, good.RunToIdle().Status);
        Assert.Contains("str:still alive", kit.Snapshot());
    }

    // ---- Test 2: pause and resume across an async call ----

    const string AwaitWrapperLuaYield = """
        local function await_thing(x)
            ossl.StartAsync(x)
            coroutine.yield()
            return ossl.TakeResult()
        end
        local r1 = await_thing(21)
        ossl.Report("got", r1)
        local ok, r2 = pcall(await_thing, 5)
        ossl.Report("got", ok, r2)
        """;

    const string AwaitWrapperCYield = """
        local function await_thing(x)
            ossl.StartAsyncYield(x)
            return ossl.TakeResult()
        end
        local r1 = await_thing(21)
        ossl.Report("got", r1)
        local ok, r2 = pcall(await_thing, 5)
        ossl.Report("got", ok, r2)
        """;

    static void DriveAsync(string source)
    {
        var (vm, kit) = NewVm();
        using var _ = vm;
        using var s = Run(vm, source, tag: 77);

        var r = s.Step();
        Assert.Equal(StepStatus.Preempted, r.Status);
        Assert.Equal(21, kit.Pending[77]);
        Assert.Empty(kit.Snapshot());

        // The host finishes the work later and resumes the script with a value.
        kit.Results[77] = Value.Int(42);
        r = s.Step();
        Assert.Equal(StepStatus.Preempted, r.Status); // second await inside pcall
        Assert.Equal(5, kit.Pending[77]);
        Assert.Equal(new[] { "str:got num:42" }, kit.Snapshot());

        kit.Results[77] = Value.Int(99);
        r = s.Step();
        Assert.Equal(StepStatus.Ok, r.Status);
        Assert.Equal(new[] { "str:got num:42", "str:got true num:99" }, kit.Snapshot());
    }

    [Fact] public void T2_AsyncViaLuaSideYieldWrapper() => DriveAsync(AwaitWrapperLuaYield);
    [Fact] public void T2_AsyncViaHostFunctionYield() => DriveAsync(AwaitWrapperCYield);

    // ---- Test 3: Ares save and restore ----

    const string SaveMidYield = """
        local t = { a = 1, list = { 10, 20 } }
        local function mk() local n = 0; return function() n += 1; return n end end
        local c = mk()
        c(); c()
        ossl.Report("before", t.a, c())
        coroutine.yield()
        t.a += 1
        ossl.Report("after", t.a, c(), t.list[2], ossl.GetGridName())
        """;

    static readonly string[] BeforeLines = { "str:before num:1 num:3" };
    static readonly string[] AfterLines = { "str:after num:2 num:4 num:20 str:Example Grid" };

    [Fact]
    public void T3_SaveMidYieldAndRestoreInFreshVm()
    {
        var (vm1, kit1) = NewVm();
        byte[] state;
        using (vm1)
        {
            using var s = Run(vm1, SaveMidYield);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            Assert.Equal(BeforeLines, kit1.Snapshot());
            state = s.Serialize();
        }
        Assert.True(state.Length > 0);

        var (vm2, kit2) = NewVm();
        using (vm2)
        {
            using var s2 = vm2.NewScript(Host.Compile(SaveMidYield, false), tag: 1);
            Assert.True(s2.Restore(state));
            Assert.Equal(StepStatus.Ok, s2.RunToIdle().Status);
            // The side effect before the save is not repeated, the rest runs on the restored locals.
            Assert.Equal(AfterLines, kit2.Snapshot());
        }
    }

    [Fact]
    public void T3_SavePreemptedLoopAndRestoreInFreshVm()
    {
        const string loop = """
            ossl.Report("start")
            local sum = 0
            for i = 1, 3000000 do sum += i end
            ossl.Report("sum", sum)
            """;
        var (vm1, kit1) = NewVm();
        byte[] state;
        using (vm1)
        {
            using var s = Run(vm1, loop);
            int preempts = 0;
            StepResult r;
            while ((r = s.Step(0.0002)).Status == StepStatus.Preempted && preempts < 3) preempts++;
            Assert.True(preempts >= 2, "loop should have been preempted at least twice");
            Assert.Equal(StepStatus.Preempted, r.Status);
            state = s.Serialize();
            Assert.Equal(new[] { "str:start" }, kit1.Snapshot());
        }
        var (vm2, kit2) = NewVm();
        using (vm2)
        {
            using var s2 = vm2.NewScript(Host.Compile(loop, false), tag: 1);
            Assert.True(s2.Restore(state));
            Assert.Equal(StepStatus.Ok, s2.RunToIdle(0.05).Status);
            Assert.Equal(new[] { "str:sum num:4500001500000" }, kit2.Snapshot());
        }
    }

    [Fact]
    public void T3_RestoreInSeparateProcessThroughFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slua-r1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string assetFile = Path.Combine(dir, "script.asset");
        string stateFile = Path.Combine(dir, "script.state");
        File.WriteAllBytes(assetFile, Host.Compile(SaveMidYield, false));

        var save = RunTool("save", assetFile, stateFile);
        Assert.Equal(0, save.exit);
        Assert.Contains("line: str:before num:1 num:3", save.output);
        Assert.True(File.Exists(stateFile));

        var restore = RunTool("restore", assetFile, stateFile);
        Assert.Equal(0, restore.exit);
        Assert.Contains("restored=True", restore.output);
        Assert.Contains("line: str:after num:2 num:4 num:20 str:Example Grid", restore.output);
        Assert.DoesNotContain("line: str:before", restore.output);
    }

    static (int exit, string output) RunTool(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SluaHost.Tool.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    [Fact]
    public void T3_RestoreWithDifferentBoundFunctionsIsRefusedAndStartsFresh()
    {
        var (vm1, kit1) = NewVm();
        byte[] state;
        using (vm1)
        {
            using var s = Run(vm1, SaveMidYield);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            state = s.Serialize();
        }
        // One extra function bound: the saved state must be refused, not half-restored.
        var (vm2, kit2) = NewVm(extra: true);
        using (vm2)
        {
            using var s2 = vm2.NewScript(Host.Compile(SaveMidYield, false), tag: 1);
            Assert.False(s2.Restore(state));
            // Fresh start: the script runs from the top and yields at the same place.
            Assert.Equal(StepStatus.Preempted, s2.Step().Status);
            Assert.Equal(BeforeLines, kit2.Snapshot());
        }
    }

    [Fact]
    public void T3_RestoreFromDifferentRuntimeCommitIsRefusedAndStartsFresh()
    {
        var (vm1, _) = NewVm();
        byte[] state;
        using (vm1)
        {
            using var s = Run(vm1, SaveMidYield);
            s.Step();
            state = s.Serialize();
        }
        // Header: "SLSH", u32 version, u16 commit length, commit bytes, u64 function hash.
        Assert.Equal("SLSH", System.Text.Encoding.ASCII.GetString(state, 0, 4));
        state[4 + 4 + 2] ^= 0x01; // first character of the recorded commit
        var (vm2, kit2) = NewVm();
        using (vm2)
        {
            using var s2 = vm2.NewScript(Host.Compile(SaveMidYield, false), tag: 1);
            Assert.False(s2.Restore(state));
            Assert.Equal(StepStatus.Preempted, s2.Step().Status);
            Assert.Equal(BeforeLines, kit2.Snapshot());
        }
    }

    [Fact]
    public void T3_GarbageStateIsAnErrorNotACrash()
    {
        var (vm, _) = NewVm();
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(SaveMidYield, false), tag: 1);
            var e = Assert.ThrowsAny<SluaException>(() => s.Restore(new byte[] { 1, 2, 3, 4, 5 }));
            Assert.True(e.Code < 0);
        }
    }

    // ---- Test 4: the watchdog ----

    [Theory]
    [InlineData(Watchdog.Resident)]
    [InlineData(Watchdog.Threaded)]
    public void T4_RunawayLoopIsCutOffAndScriptCanBeKilled(Watchdog mode)
    {
        long violationsBefore = Host.BusyViolations();
        var (vm, kit) = NewVm(mode);
        using var _ = vm;
        using var runaway = Run(vm, "while true do end", tag: 1);
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = runaway.Step(0.002);
            sw.Stop();
            Assert.Equal(StepStatus.Preempted, r.Status);
            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"step took {sw.Elapsed}");
        }
        runaway.Kill();
        var (kind, _) = runaway.Fault();
        Assert.Equal(FaultKind.Unknown, kind);
        Assert.Equal(StepStatus.Fault, runaway.Step(0.002).Status);

        // The host and the other scripts in the same VM are healthy.
        using var other = Run(vm, "ossl.Report('healthy')", tag: 2);
        Assert.Equal(StepStatus.Ok, other.RunToIdle().Status);
        Assert.Contains("str:healthy", kit.Snapshot());
        Assert.Equal(violationsBefore, Host.BusyViolations());
    }

    [Theory]
    [InlineData(Watchdog.Resident)]
    [InlineData(Watchdog.Threaded)]
    public void T4_LoopThatCannotYieldIsKilledByTheEngineWithTimeout(Watchdog mode)
    {
        var (vm, kit) = NewVm(mode);
        using var _ = vm;
        // A metamethod call is a boundary the engine cannot yield across (pcall, table.sort and
        // string.gsub are not: they yield and are preempted). The engine raises an error at
        // 3.5 times the quanta and, if the script is still stuck, kills it with a Timeout fault.
        using (var stuck = Run(vm, "local t = setmetatable({}, { __index = function() while true do end end }); local x = t.a", tag: 1))
        {
            var sw = Stopwatch.StartNew();
            var r = stuck.Step(0.002);
            sw.Stop();
            Assert.Equal(StepStatus.Fault, r.Status);
            Assert.Equal(FaultKind.Timeout, stuck.Fault().Kind);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        }
        using var other = Run(vm, "ossl.Report('healthy')", tag: 2);
        Assert.Equal(StepStatus.Ok, other.RunToIdle().Status);
    }

    [Theory]
    [InlineData(Watchdog.Resident)]
    [InlineData(Watchdog.Threaded)]
    public void T4_ScriptThatCatchesTheTimeoutErrorIsPunishedWithSleepAndHostCanKillIt(Watchdog mode)
    {
        var (vm, kit) = NewVm(mode);
        using var _ = vm;
        // The error the engine raises is catchable. A script that catches it and carries on is not
        // killed by the engine: each window ends at a yield point instead and the script is made to
        // sleep in proportion to its overrun. The host stays in control and can kill it.
        using var defiant = Run(vm, "while true do pcall(function() return setmetatable({}, { __index = function() while true do end end }).a end) end", tag: 1);
        float slept = 0;
        for (int i = 0; i < 6; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = defiant.Step(0.002);
            sw.Stop();
            Assert.Equal(StepStatus.Preempted, r.Status);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"step took {sw.Elapsed}");
            slept += r.SleepServed;
        }
        Assert.True(slept > 0.05f, $"expected punishment sleep, got {slept}s");
        defiant.Kill();
        Assert.Equal(StepStatus.Fault, defiant.Step(0.002).Status);

        using var other = Run(vm, "ossl.Report('healthy')", tag: 2);
        Assert.Equal(StepStatus.Ok, other.RunToIdle().Status);
    }

    // ---- Test 5: the memory cap ----

    [Fact]
    public void T5_ScriptPastTheCapFaultsWithMemoryErrorAndOthersAreUnaffected()
    {
        var (vm, kit) = NewVm();
        using var _ = vm;
        using var hog = Run(vm, """
            local t = {}
            for i = 1, 1000000 do t[i] = { i, i } end
            """, tag: 1);
        Assert.Equal(128 * 1024, hog.MemoryLimit);
        var r = hog.RunToIdle(0.05);
        Assert.Equal(StepStatus.Fault, r.Status);
        var (kind, msg) = hog.Fault();
        Assert.Equal(FaultKind.OutOfMemory, kind);
        Assert.Contains("memory", msg, StringComparison.OrdinalIgnoreCase);

        using var other = Run(vm, "ossl.Report('fine')", tag: 2);
        Assert.Equal(StepStatus.Ok, other.RunToIdle().Status);
        Assert.Contains("str:fine", kit.Snapshot());
        Assert.True(other.UsedMemory > 0 && other.UsedMemory < other.MemoryLimit);

        var (vm2, kit2) = NewVm();
        using (vm2)
        {
            using var s2 = Run(vm2, "ossl.Report('other vm')");
            Assert.Equal(StepStatus.Ok, s2.RunToIdle().Status);
            Assert.Contains("str:other vm", kit2.Snapshot());
        }
    }

    // ---- Test 6: LSL compiled by LL's compiler ----

    const string LslTwoStates = """
        integer n = 0;
        default {
            state_entry() {
                llOwnerSay("hello " + (string)llGetUnixTime());
                n += 1;
                state second;
            }
            state_exit() { llSay(0, "leaving default"); }
        }
        state second {
            state_entry() { llSay(0, "in second " + (string)n); }
        }
        """;

    [Fact]
    public void T6_LslRunsThroughSameLayerAndCallsLlFunctionsInDotNet()
    {
        var (vm, kit) = NewVm();
        using var _ = vm;
        using var s = Run(vm, LslTwoStates, lsl: true);
        var r = s.RunToIdle();
        Assert.Equal(StepStatus.Ok, r.Status);
        Assert.Equal(new[]
        {
            "OwnerSay str:hello 1700000000",
            "Say int:0 str:leaving default",
            "Say int:0 str:in second 1",
        }, kit.Snapshot());
        // LL's default for LSL on Luau, recorded for the report.
        Assert.Equal(128 * 1024, s.MemoryLimit);
    }

    [Fact]
    public void T6_LslCallToOsFunctionOutcomeIsRecorded()
    {
        const string src = "default { state_entry() { llOwnerSay(osGetGridName()); } }";
        Host.Init(); // an init failure must fail the test, not be recorded as a compile error
        string outcome;
        try
        {
            byte[] asset = Host.Compile(src, true);
            // Compiled: find out which name it would call at run time.
            string text = System.Text.Encoding.Latin1.GetString(asset);
            outcome = "COMPILED bytes=" + asset.Length
                + " contains'GetGridName'=" + text.Contains("GetGridName")
                + " contains'osGetGridName'=" + text.Contains("osGetGridName");
        }
        catch (SluaException e)
        {
            outcome = "COMPILE ERROR: " + e.Message;
        }
        Measure.Record("t6-os-call", outcome);
        Assert.False(string.IsNullOrEmpty(outcome));
    }
}
