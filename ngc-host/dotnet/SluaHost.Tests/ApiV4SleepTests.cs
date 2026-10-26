// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// API version 4, delays: a delay is reported by the step in which the script yields for it, and a
/// host call that asks for a delay and gives back no value ends the script's turn.
/// </summary>
public class ApiV4SleepTests
{
    // ---- A: the delay is reported in the step that yields for it ----

    [Fact]
    public void Delay_TheDelayOfACallIsReportedByTheStepThatMadeIt()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            ossl.SlowEcho(0.25, "x")
            coroutine.yield()
            ossl.Report("after")
            """);
        var first = s.Step();
        Assert.Equal(StepStatus.Preempted, first.Status);
        Assert.Equal(0.25f, first.SleepServed, 3);
        var second = s.Step();
        Assert.Equal(0f, second.SleepServed);
    }

    [Fact]
    public void Delay_ADelayIsReportedOnceNotAgainByTheNextStep()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            ossl.SlowEcho(0.5, "x")
            coroutine.yield()
            coroutine.yield()
            ossl.Report("end")
            """);
        float total = 0;
        for (int i = 0; i < 6; i++)
        {
            var r = s.Step();
            total += r.SleepServed;
            if (r.Status == StepStatus.Idle) break;
        }
        Assert.Equal(0.5f, total, 3);
    }

    [Fact]
    public void Delay_AHostFunctionThatYieldsItselfReportsItsDelayInThatStep()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            ossl.SlowYield(0.3)
            ossl.Report("after")
            """);
        var first = s.Step();
        Assert.Equal(StepStatus.Preempted, first.Status);
        Assert.Equal(0.3f, first.SleepServed, 3);
        Assert.Empty(rig.Kit.Snapshot());
        var second = s.Step();
        Assert.Equal(StepStatus.Ok, second.Status);
        Assert.Equal(0f, second.SleepServed);
    }

    [Fact]
    public void Delay_ADelayedLslCallIsReportedByTheStepThatMadeIt()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            default {
                state_entry() {
                    llSay(0, "one");
                    llOwnerSay("two");
                }
            }
            """, lsl: true);
        var results = rig.StepUntilLines(s, l => l.Contains("Say int:0 str:one"));
        Assert.Equal(0.2f, results[^1].SleepServed, 3);
    }

    [Fact]
    public void Delay_ADispatchedHandlerReportsItsDelayInTheDispatch()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function()
                ossl.SlowEcho(0.4, "x")
                coroutine.yield()
                ossl.Report("handled")
            end)
            """);
        s.RunToIdle();
        var r = s.Dispatch("touch_start", new[] { Value.Int(1) });
        Assert.Equal(StepStatus.Preempted, r.Status);
        Assert.Equal(0.4f, r.SleepServed, 3);
    }

    // ---- B: a delayed call that gives back nothing ends the turn ----

    [Fact]
    public void Yield_ADelayedCallThatReturnsNothingEndsTheScriptsTurn()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            ossl.Slow(0.5)
            ossl.Report("after")
            """);
        var first = s.Step();
        Assert.Equal(StepStatus.Preempted, first.Status);
        Assert.Equal(0.5f, first.SleepServed, 3);
        Assert.Empty(rig.Kit.Snapshot());
        var second = s.Step();
        Assert.Equal(StepStatus.Ok, second.Status);
        Assert.Equal(0f, second.SleepServed);
        Assert.Equal(new[] { "str:after" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void Yield_LlSleepEndsTheScriptsTurnInLsl()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            default {
                state_entry() {
                    llSleep(2.0);
                    llOwnerSay("b");
                }
            }
            """, lsl: true);
        var results = new List<StepResult>();
        int sleepStep = -1;
        for (int i = 0; i < 20 && sleepStep < 0; i++)
        {
            var r = s.Step(0.05);
            results.Add(r);
            if (r.SleepServed > 0) sleepStep = i;
        }
        Assert.True(sleepStep >= 0, "no step reported the llSleep");
        Assert.Equal(2.0f, results[sleepStep].SleepServed, 3);
        Assert.DoesNotContain("OwnerSay str:b", rig.Kit.Snapshot());
        rig.StepUntilLines(s, l => l.Contains("OwnerSay str:b"));
    }

    [Fact]
    public void Yield_ADelayedCallThatReturnsAValueKeepsItsValue()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("ossl.Report(ossl.SlowEcho(0.1, 'kept'))");
        float sleep = 0;
        for (int i = 0; i < 10; i++)
        {
            var r = s.Step();
            sleep += r.SleepServed;
            if (r.Status == StepStatus.Idle) break;
        }
        Assert.Equal(0.1f, sleep, 3);
        Assert.Equal(new[] { "str:kept" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void Yield_AScriptWaitingOutADelayCanBeSavedAndRestored()
    {
        const string source = """
            ossl.Report("before")
            ossl.Slow(0.25)
            ossl.Report("after")
            """;
        byte[] state;
        using (var rig = new V4Rig())
        {
            using var s = rig.Run(source);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            Assert.Equal(new[] { "str:before" }, rig.Kit.Snapshot());
            state = s.Serialize();
        }
        using (var rig2 = new V4Rig())
        {
            using var s2 = rig2.Fresh(Host.Compile(source, false));
            Assert.True(s2.Restore(state));
            for (int i = 0; i < 10 && s2.Step().Status != StepStatus.Idle; i++) { }
            Assert.Equal(new[] { "str:after" }, rig2.Kit.Snapshot());
        }
    }
}
