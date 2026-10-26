// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// API version 4, handler mask: SLua registers its event handlers while it runs, so the library
/// reports the set of events a script handles on request and says when a step changed it, which lets
/// a region keep its per-prim event flags current.
/// </summary>
public class ApiV4HandlerMaskTests
{
    static ulong Bit(string name) => Host.EventBit(name);

    [Fact]
    public void HandlerMask_AnEventsBitIsItsIndexMinusOneShifted()
    {
        Assert.Equal(1UL << (Host.EventIndex("touch_start") - 1), Bit("touch_start"));
        Assert.NotEqual(Bit("touch_start"), Bit("timer"));
        Assert.Equal(0UL, Bit("touch_start") & Bit("timer"));
        Assert.Throws<SluaException>(() => Host.EventBit("no_such_event"));
    }

    [Fact]
    public void HandlerMask_ASLuaScriptHasNoHandlersAndAnIncompleteSetUntilItsMainChunkHasRun()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function() end)
            """);
        var (mask, known) = s.Handlers();
        Assert.Equal(0UL, mask);
        Assert.False(known);
        s.RunToIdle();
        var (after, afterKnown) = s.Handlers();
        Assert.Equal(Bit("touch_start"), after);
        Assert.True(afterKnown);
    }

    [Fact]
    public void HandlerMask_ASLuaScriptsHandlersAreReportedAfterItsMainChunkRuns()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function() end)
            LLEvents:on("timer", function() end)
            """);
        var first = s.Step();
        Assert.True(first.HandlersChanged);
        var (mask, known) = s.Handlers();
        Assert.Equal(Bit("touch_start") | Bit("timer"), mask);
        Assert.True(known);
    }

    [Fact]
    public void HandlerMask_AStepThatChangedNothingDoesNotSayItChangedTheSet()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function() end)
            """);
        Assert.True(s.Step().HandlersChanged);
        var idle = s.Step();
        Assert.Equal(StepStatus.Idle, idle.Status);
        Assert.False(idle.HandlersChanged);
    }

    [Fact]
    public void HandlerMask_AHandlerThatRemovesItselfIsReportedByTheDispatchThatRanIt()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            local function once()
                LLEvents:off("touch_start", once)
            end
            LLEvents:on("touch_start", once)
            LLEvents:on("timer", function() end)
            """);
        Assert.True(s.Step().HandlersChanged);
        Assert.Equal(Bit("touch_start") | Bit("timer"), s.HandlerMask);
        var r = s.Dispatch("touch_start", new[] { Value.Int(1) });
        Assert.True(r.HandlersChanged);
        Assert.Equal(Bit("timer"), s.HandlerMask);
        var again = s.Dispatch("timer", Array.Empty<Value>());
        Assert.False(again.HandlersChanged);
    }

    [Fact]
    public void HandlerMask_AHandlerThatRegistersAnotherIsReportedByTheDispatchThatRanIt()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function()
                LLEvents:on("listen", function() end)
            end)
            """);
        s.RunToIdle();
        Assert.Equal(Bit("touch_start"), s.HandlerMask);
        var r = s.Dispatch("touch_start", new[] { Value.Int(1) });
        Assert.True(r.HandlersChanged);
        Assert.Equal(Bit("touch_start") | Bit("listen"), s.HandlerMask);
    }

    [Fact]
    public void HandlerMask_ALslScriptHasTheMaskOfItsCurrentStateAndAStateChangeIsReported()
    {
        using var rig = new V4Rig();
        using var s = rig.Run("""
            default {
                state_entry() { state other; }
                touch_start(integer n) { }
            }
            state other {
                listen(integer c, string n, key id, string m) { }
                timer() { }
            }
            """, lsl: true);
        var (mask, known) = s.Handlers();
        Assert.True(known);
        Assert.Equal(Bit("state_entry") | Bit("touch_start"), mask);
        // The state change happens while the script runs to idle; some step on the way reports the new set.
        bool changed = false;
        for (int i = 0; i < 20; i++)
        {
            var r = s.Step();
            changed |= r.HandlersChanged;
            if (r.Status == StepStatus.Idle) break;
        }
        Assert.True(changed, "no step reported the change of state's handler set");
        Assert.Equal(Bit("listen") | Bit("timer"), s.HandlerMask);
    }

    // A script that has finished its main chunk and is paused inside a handler, so a restore of it
    // changes the handler set the host knows and nothing else about what is complete.
    const string PausedInAHandler = """
        LLEvents:on("touch_start", function() coroutine.yield() end)
        LLEvents:on("sensor", function() end)
        """;

    static byte[] SavePausedInAHandler()
    {
        using var rig = new V4Rig();
        using var s = rig.Run(PausedInAHandler);
        s.RunToIdle();
        Assert.Equal(StepStatus.Preempted, s.Dispatch("touch_start", new[] { Value.Int(1) }).Status);
        return s.Serialize();
    }

    [Fact]
    public void HandlerMask_ARestoredScriptsSetIsReportedByItsFirstStep()
    {
        byte[] state = SavePausedInAHandler();
        using var rig2 = new V4Rig();
        using var s2 = rig2.Fresh(Host.Compile(PausedInAHandler, false));
        Assert.True(s2.Restore(state));
        var first = s2.Step();
        Assert.True(first.HandlersChanged);
        Assert.Equal(Bit("touch_start") | Bit("sensor"), s2.HandlerMask);
        Assert.True(s2.HandlersKnown);
    }

    [Fact]
    public void HandlerMask_ReadingTheMaskCountsAsTellingTheHost()
    {
        byte[] state = SavePausedInAHandler();
        using var rig2 = new V4Rig();
        using var s2 = rig2.Fresh(Host.Compile(PausedInAHandler, false));
        Assert.True(s2.Restore(state));
        // The host reads the restored set before it steps the script, so the step has nothing new to say.
        Assert.Equal(Bit("touch_start") | Bit("sensor"), s2.HandlerMask);
        Assert.False(s2.Step().HandlersChanged);
    }
}
