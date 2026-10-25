// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

public class SoakTests
{
    /// <summary>
    /// Creates, runs and frees a VM, a shared image and a script 10,000 times, sampling the
    /// process's resident memory every 1,000 cycles. The pass rule is that growth levels off:
    /// the second half grows by no more than 1 MiB or a fifth of what the first half grew.
    /// Resident memory is only a stable figure on Linux, so other systems record the series
    /// and do not assert on it.
    /// </summary>
    [Fact]
    public void A3_TenThousandCyclesLevelOff()
    {
        byte[] asset = Host.Compile("""
            local t = {}
            for i = 1, 200 do t[i] = { i } end
            LLEvents:on("dataserver", function(query, data) ossl.Report(data) end)
            """, false);
        void Cycle()
        {
            var kit = new TestKit();
            using var vm = new Vm(Watchdog.Resident);
            kit.Register(vm);
            using var image = vm.NewImage(asset);
            using var s = image.NewScript(tag: 1);
            s.Start();
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(StepStatus.Ok, s.Dispatch("dataserver", new[] { Value.Key("12345678-9abc-def0-1234-56789abcdef0"), Value.Str("x") }).Status);
        }

        for (int i = 0; i < 50; i++) Cycle(); // warm up allocator and JIT
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var samples = new List<long> { Measure.RssBytes() };
        for (int block = 0; block < 10; block++)
        {
            for (int i = 0; i < 1000; i++) Cycle();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            samples.Add(Measure.RssBytes());
        }
        Measure.Record("a3-rss-10000-cycles-KiB", string.Join(" ", samples.Select(x => x / 1024)));

        long firstHalf = samples[5] - samples[0], secondHalf = samples[10] - samples[5];
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(secondHalf <= Math.Max(1024 * 1024, firstHalf / 5),
            $"second half grew {secondHalf / 1024} KiB, first half {firstHalf / 1024} KiB: {string.Join(" ", samples.Select(x => x / 1024))}");
    }
}
