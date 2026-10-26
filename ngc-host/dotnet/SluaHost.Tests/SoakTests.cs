// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// The rule a soak run is judged by. The figures are the library's own live counts of VMs, images,
/// scripts and unreleased blocks (see Host.GetAllocStats), which do not depend on when the system
/// allocator grows the process. After each block of cycles, with everything released, every count
/// must be back at what it was before the first block.
/// </summary>
public static class SoakRule
{
    public static string? Check(IReadOnlyList<AllocStats> samples)
    {
        var b = samples[0];
        for (int i = 1; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.LiveVms > b.LiveVms || s.LiveImages > b.LiveImages || s.LiveScripts > b.LiveScripts ||
                s.OutstandingBlocks > b.OutstandingBlocks || s.OutstandingBytes > b.OutstandingBytes)
                return $"sample {i} {s} is above the baseline {b}";
        }
        return null;
    }
}

public unsafe class SoakTests
{
    static readonly byte[] Asset = Host.Compile("""
        local t = {}
        for i = 1, 200 do t[i] = { i } end
        LLEvents:on("dataserver", function(query, data) ossl.Report(data) end)
        """, false);

    static readonly List<nint> Dropped = new();

    enum Leak { None, KeepScript, DropBlock }

    static void Cycle(Leak leak, List<IDisposable> kept)
    {
        var kit = new TestKit();
        var vm = new Vm(Watchdog.Resident);
        kit.Register(vm);
        var image = vm.NewImage(Asset);
        var s = image.NewScript(tag: 1);
        s.Start();
        Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        Assert.Equal(StepStatus.Ok, s.Dispatch("dataserver", new[] { Value.Key("12345678-9abc-def0-1234-56789abcdef0"), Value.Str("x") }).Status);
        if (leak == Leak.DropBlock)
        {
            byte* p = null; nuint len = 0;
            Host.Check("slua_serialize", Native.slua_serialize(s.Handle, &p, &len)); // never released with slua_free
            Dropped.Add((nint)p);
        }
        if (leak == Leak.KeepScript) { kept.Add(vm); kept.Add(image); kept.Add(s); return; }
        s.Dispose(); image.Dispose(); vm.Dispose();
    }

    static AllocStats Settle()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        return Host.GetAllocStats();
    }

    static List<AllocStats> Run(Leak leak, int blocks, int perBlock, List<IDisposable> kept)
    {
        for (int i = 0; i < 50; i++) Cycle(Leak.None, kept); // warm up caches and lazy statics
        var samples = new List<AllocStats> { Settle() };
        for (int block = 0; block < blocks; block++)
        {
            for (int i = 0; i < perBlock; i++) Cycle(leak, kept);
            samples.Add(Settle());
        }
        return samples;
    }

    [Fact]
    public void A3_TheCountersSeeWhatIsLive()
    {
        var before = Settle();
        byte* p = null; nuint len = 0;
        AllocStats during;
        using (var vm = new Vm(Watchdog.Resident))
        {
            new TestKit().Register(vm);
            using var image = vm.NewImage(Asset);
            using var s = image.NewScript(tag: 1);
            s.Start();
            Host.Check("slua_serialize", Native.slua_serialize(s.Handle, &p, &len));
            during = Host.GetAllocStats();
        }
        Assert.Equal(before.LiveVms + 1, during.LiveVms);
        Assert.Equal(before.LiveImages + 1, during.LiveImages);
        Assert.Equal(before.LiveScripts + 1, during.LiveScripts);
        Assert.Equal(before.OutstandingBlocks + 1, during.OutstandingBlocks);
        Assert.Equal(before.OutstandingBytes + (ulong)len, during.OutstandingBytes);

        Native.slua_free(p);
        var after = Settle();
        Assert.Equal(before.LiveVms, after.LiveVms);
        Assert.Equal(before.LiveImages, after.LiveImages);
        Assert.Equal(before.LiveScripts, after.LiveScripts);
        Assert.Equal(before.OutstandingBlocks, after.OutstandingBlocks);
        Assert.Equal(before.OutstandingBytes, after.OutstandingBytes);
    }

    /// <summary>10,000 create-run-free cycles; the live counts must come back to the baseline after every 1,000.</summary>
    [Fact]
    public void A3_TenThousandCyclesLevelOff()
    {
        var kept = new List<IDisposable>();
        long rss0 = Measure.RssBytes();
        var samples = Run(Leak.None, 10, 1000, kept);
        // Resident memory and the C heap figure are reported for information; they are not the rule.
        Measure.Record("a3-rss-KiB", $"{rss0 / 1024} -> {Measure.RssBytes() / 1024}");
        Measure.Record("a3-heap-in-use-KiB", string.Join(" ", samples.Select(x => x.HeapInUseBytes / 1024)));
        Measure.Record("a3-live-counts", string.Join(" | ", samples.Select(x => $"{x.LiveVms}/{x.LiveImages}/{x.LiveScripts}/{x.OutstandingBlocks}/{x.OutstandingBytes}")));
        Assert.Null(SoakRule.Check(samples));
    }

    [Fact]
    public void A3_AScriptKeptAliveInEachCycleFailsTheRule()
    {
        var kept = new List<IDisposable>();
        try { Assert.NotNull(SoakRule.Check(Run(Leak.KeepScript, 3, 100, kept))); }
        finally { for (int i = kept.Count - 1; i >= 0; i--) kept[i].Dispose(); }
    }

    [Fact]
    public void A3_ABlockNeverReleasedInEachCycleFailsTheRule()
    {
        var kept = new List<IDisposable>();
        var samples = Run(Leak.DropBlock, 3, 100, kept);
        // Release the blocks so the counters are back where they were for later tests.
        foreach (var p in Dropped) Native.slua_free((void*)p);
        Dropped.Clear();
        Assert.NotNull(SoakRule.Check(samples));
    }
}
