// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Diagnostics;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// API version 4, CPU time: every step and dispatch reports the CPU time the calling thread used while
/// the script ran, so a scheduler can tell a script that burns CPU from one the operating system
/// kept off the processor.
/// </summary>
public class ApiV4CpuTimeTests
{
    // Enough work to take well over 100 ms of CPU on any machine this is built for.
    const string Burn = """
        local x = 0
        for i = 1, 40000000 do x = x + 1 end
        ossl.Report("done")
        """;

    [Fact]
    public void CpuTime_AStepReportsTheCpuTimeTheScriptUsed()
    {
        using var rig = new V4Rig();
        using var s = rig.Run(Burn);
        var wall = Stopwatch.StartNew();
        var r = s.Step(30.0);
        wall.Stop();
        Assert.Equal(StepStatus.Ok, r.Status);
        Assert.True(r.CpuSeconds >= 0.1, $"cpu {r.CpuSeconds} s for a step that took {wall.Elapsed.TotalSeconds} s");
        // The thread cannot have used more CPU than the time that passed; the allowance covers the
        // coarse tick some systems account thread time in.
        Assert.True(r.CpuSeconds <= wall.Elapsed.TotalSeconds + 0.05, $"cpu {r.CpuSeconds} s, wall {wall.Elapsed.TotalSeconds} s");
    }

    [Fact]
    public void CpuTime_ADispatchReportsTheCpuTimeTheHandlerUsed()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function()
                local x = 0
                for i = 1, 40000000 do x = x + 1 end
            end)
            """);
        s.RunToIdle();
        var wall = Stopwatch.StartNew();
        var r = s.Dispatch("touch_start", new[] { Value.Int(1) }, 30.0);
        wall.Stop();
        Assert.Equal(StepStatus.Ok, r.Status);
        Assert.True(r.CpuSeconds >= 0.1, $"cpu {r.CpuSeconds} s for a dispatch that took {wall.Elapsed.TotalSeconds} s");
        Assert.True(r.CpuSeconds <= wall.Elapsed.TotalSeconds + 0.05, $"cpu {r.CpuSeconds} s, wall {wall.Elapsed.TotalSeconds} s");
    }

    [Fact]
    public void CpuTime_TimeSpentBlockedInAHostCallIsNotCpuTime()
    {
        using var rig = new V4Rig();
        rig.Vm.Register("ossl", "Block", (tag, a) => { Thread.Sleep(300); return null; });
        using var s = rig.Run("ossl.Block()");
        var wall = Stopwatch.StartNew();
        var r = s.Step(30.0);
        wall.Stop();
        Assert.True(wall.Elapsed.TotalSeconds >= 0.25, "the host call did not block as the test expects");
        Assert.True(r.CpuSeconds < 0.15, $"cpu {r.CpuSeconds} s for a step that spent {wall.Elapsed.TotalSeconds} s mostly blocked");
    }

    [Fact]
    public void CpuTime_AStepThatRunsNothingReportsNone()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("ossl.Report('x')");
        s.RunToIdle();
        var r = s.Step();
        Assert.Equal(StepStatus.Idle, r.Status);
        Assert.Equal(0.0, r.CpuSeconds);
    }

    [Fact]
    public unsafe void CpuTime_TheStepResultIs32BytesWithTheFieldsWhereTheHeaderPutsThem()
    {
        Assert.Equal(32, Host.NativeStructSize(1));
        Assert.Equal(32, sizeof(Native.StepResultN));
        Native.StepResultN r = default;
        byte* b = (byte*)&r;
        Assert.Equal(8, (int)((byte*)&r.SleepServed - b));
        Assert.Equal(16, (int)((byte*)&r.CpuSeconds - b));
        Assert.Equal(24, (int)((byte*)&r.HandlersChanged - b));
    }
}
