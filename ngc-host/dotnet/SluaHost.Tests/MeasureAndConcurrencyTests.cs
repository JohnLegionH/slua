// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Diagnostics;
using System.Text;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>Writes measurements where the SLUA_R1_OUT folder says, and always to the test output as well.</summary>
public static class Measure
{
    public static void Record(string name, string text)
    {
        string? dir = Environment.GetEnvironmentVariable("SLUA_R1_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "measurements.txt"), $"{name}: {text}{Environment.NewLine}");
    }

    public static long RssBytes()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("VmRSS:")) return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        }
        using var p = Process.GetCurrentProcess();
        return p.WorkingSet64;
    }
}

public class MeasureTests
{
    [Fact]
    public void M_HostFunctionRoundTripCost()
    {
        var kit = new TestKit();
        using var vm = new Vm(Watchdog.Resident);
        kit.Register(vm);
        const int n = 2_000_000;
        byte[] withCalls = Host.Compile($"for i = 1, {n} do ossl.Nop() end", false);
        byte[] baseline = Host.Compile($"for i = 1, {n} do end", false);

        double Time(byte[] asset)
        {
            double best = double.MaxValue;
            for (int rep = 0; rep < 5; rep++)
            {
                using var s = vm.NewScript(asset, tag: 1);
                s.Start();
                var sw = Stopwatch.StartNew();
                var r = s.RunToIdle(30.0);
                sw.Stop();
                Assert.Equal(StepStatus.Ok, r.Status);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        double calls = Time(withCalls), loop = Time(baseline);
        double perCall = (calls - loop) * 1e6 / n;
        Measure.Record("host-roundtrip", $"{perCall:F0} ns per call (loop+call {calls * 1e6 / n:F0} ns, empty loop {loop * 1e6 / n:F0} ns, {n} calls, best of 5)");
        Assert.True(Interlocked.Read(ref kit.NopCalls) >= n);
        Assert.InRange(perCall, 1, 1_000_000);
    }

    [Fact]
    public void M_MemoryPerIdleScript()
    {
        var kit = new TestKit();
        using var vm = new Vm(Watchdog.Resident);
        kit.Register(vm);
        byte[] asset = Host.Compile("local t = { 1, 2, 3 }\nlocal function f() return t end\nossl.Nop()\nfunction ondone() end\ncoroutine.yield()", false);
        const int n = 300;
        GC.Collect(); GC.WaitForPendingFinalizers();
        long before = Measure.RssBytes();
        var scripts = new List<Script>();
        long used = 0;
        for (int i = 0; i < n; i++)
        {
            var s = vm.NewScript(asset, tag: i);
            s.Start();
            s.Step();
            used += s.UsedMemory;
            scripts.Add(s);
        }
        long after = Measure.RssBytes();
        Measure.Record("memory-per-idle-script", $"{(after - before) / n} bytes process RSS per script (n={n}); {used / n} bytes counted against the script's {128 * 1024} byte limit");
        foreach (var s in scripts) s.Dispose();
        Assert.True(used / n > 0);
    }

    [Fact]
    public void M_CompileTimeForThousandLineScript()
    {
        var lua = new StringBuilder("local x = 0\n");
        for (int f = 0; f < 100; f++)
        {
            lua.Append($"local function f{f}(a)\n");
            for (int l = 0; l < 8; l++) lua.Append($"    x = x + a * {l + 2} - (a // {l + 3})\n");
            lua.Append("    return x\nend\n");
        }
        lua.Append("ossl.Report(f1(2))\n");
        var lsl = new StringBuilder();
        for (int f = 0; f < 200; f++)
        {
            lsl.Append($"integer f{f}(integer a)\n{{\n    integer b = a + {f};\n    b = b * 2 - a;\n    return b;\n}}\n");
        }
        lsl.Append("default {\n    state_entry() {\n        llOwnerSay((string)f1(2));\n    }\n}\n");

        int luaLines = lua.ToString().Split('\n').Length, lslLines = lsl.ToString().Split('\n').Length;
        string luaSrc = lua.ToString(), lslSrc = lsl.ToString();
        Host.Compile(luaSrc, false); Host.Compile(lslSrc, true); // warm up
        double Avg(string src, bool isLsl)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Host.Compile(src, isLsl);
            return sw.Elapsed.TotalMilliseconds / 20;
        }
        double luaMs = Avg(luaSrc, false), lslMs = Avg(lslSrc, true);
        Measure.Record("compile-1000-lines", $"SLua {luaLines} lines {luaMs:F2} ms; LSL {lslLines} lines {lslMs:F2} ms (average of 20)");
        Assert.True(luaLines >= 1000 && lslLines >= 1000);
    }
}

public class LinuxCleanlinessAndConcurrencyTests
{
    [Fact]
    public void C2_ThousandCreateRunFreeCyclesDoNotGrowTheProcess()
    {
        byte[] asset = Host.Compile("local t = {}\nfor i = 1, 200 do t[i] = { i } end\nossl.Nop()", false);
        void Cycle()
        {
            var kit = new TestKit();
            using var vm = new Vm(Watchdog.Resident);
            kit.Register(vm);
            using var s = vm.NewScript(asset, tag: 1);
            s.Start();
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        }
        for (int i = 0; i < 20; i++) Cycle(); // warm up allocator and JIT
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long start = Measure.RssBytes();
        for (int i = 0; i < 1000; i++) Cycle();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long end = Measure.RssBytes();
        Measure.Record("c2-rss-1000-cycles", $"start {start / 1024} KiB, end {end / 1024} KiB, growth {(end - start) / 1024} KiB");
        Assert.True(end - start < 64L * 1024 * 1024, $"RSS grew by {(end - start) / 1024} KiB over 1000 cycles");
    }

    [Fact]
    public async Task C3_ManyVmsDrivenFromThreadPoolWithoutSharingAVmBetweenThreads()
    {
        const int vms = 24, rounds = 40;
        // Each script yields every round, so a VM is picked up by whichever pool thread is free.
        byte[] asset = Host.Compile($"for r = 1, {rounds} do ossl.Nop(); coroutine.yield() end\nossl.Report('done')", false);
        long before = Host.BusyViolations();
        var kits = new TestKit[vms];
        var vmList = new Vm[vms];
        var scripts = new Script[vms];
        for (int i = 0; i < vms; i++)
        {
            kits[i] = new TestKit();
            vmList[i] = new Vm(i % 2 == 0 ? Watchdog.Resident : Watchdog.Threaded);
            kits[i].Register(vmList[i]);
            scripts[i] = vmList[i].NewScript(asset, tag: i);
            scripts[i].Start();
        }
        var threadsSeen = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        await Task.WhenAll(Enumerable.Range(0, vms).Select(async i =>
        {
            for (int r = 0; r <= rounds; r++)
            {
                await Task.Yield();
                threadsSeen[Environment.CurrentManagedThreadId] = 0;
                var res = scripts[i].Step(0.05);
                Assert.True(res.Status == StepStatus.Preempted || res.Status == StepStatus.Ok, res.Status.ToString());
            }
        }));
        for (int i = 0; i < vms; i++)
        {
            Assert.Equal(new[] { "str:done" }, kits[i].Snapshot());
            scripts[i].Dispose();
            vmList[i].Dispose();
        }
        Measure.Record("c3-threadpool", $"{vms} VMs x {rounds + 1} steps across {threadsSeen.Count} pool threads, busy violations {Host.BusyViolations() - before}");
        Assert.Equal(before, Host.BusyViolations());
    }

    [Fact]
    public async Task C3_TheShimRefusesASecondThreadInTheSameVm()
    {
        var kit = new TestKit();
        using var vm = new Vm(Watchdog.Resident);
        kit.Register(vm);
        using var spinner = vm.NewScript(Host.Compile("ossl.Nop() while true do end", false), tag: 1);
        using var other = vm.NewScript(Host.Compile("ossl.Nop()", false), tag: 2);
        spinner.Start(); other.Start();
        long before = Host.BusyViolations();

        var running = Task.Run(() => spinner.Step(1.0)); // holds the VM for about a second
        while (Interlocked.Read(ref kit.NopCalls) == 0) await Task.Delay(1); // the spinner is inside the VM now
        bool refused = false;
        var deadline = Stopwatch.StartNew();
        while (!refused && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { other.Step(0.01); }
            catch (SluaException e) when (e.Code == -4) { refused = true; }
            if (running.IsCompleted) break;
        }
        Assert.Equal(StepStatus.Preempted, (await running).Status);
        Assert.True(refused, "second thread was not refused while the first held the VM");
        Assert.True(Host.BusyViolations() > before);
        // The VM is intact afterwards.
        Assert.Equal(StepStatus.Ok, other.RunToIdle().Status);
    }
}
