// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Collections.Concurrent;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>A script that catches an error with pcall and then calls a host function that makes it wait goes on to get the answer.</summary>
public class CaughtErrorThenYieldTests
{
    sealed class Rig : IDisposable
    {
        public readonly Vm Vm = new(Watchdog.Resident);
        public readonly TestKit Kit = new();
        public readonly ConcurrentDictionary<long, Script> Scripts = new();
        public readonly ConcurrentDictionary<long, Value[]> Results = new();

        public Rig()
        {
            Kit.Register(Vm);
            Vm.Register("ossl", "Slow", (tag, a) => { Scripts[tag].AddSleep(a[0].N); return null; });
            Vm.RegisterPausing("ll", "GetNotecardLineSync",
                (tag, a) => new[] { Value.Str("ignored") },
                (tag, a) => Results.TryRemove(tag, out var v) ? v : new[] { Value.Str("missing") });
        }

        public Script Run(string source, long tag = 1)
        {
            var s = Vm.NewScript(Host.Compile(source, false), tag: tag);
            Scripts[tag] = s;
            s.Start();
            return s;
        }

        public void Dispose()
        {
            foreach (var s in Scripts.Values) s.Dispose();
            Vm.Dispose();
        }
    }

    [Fact]
    public void APausingCallAfterAnErrorCaughtInALuaFunctionReturnsTheAnswer()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            local ok, e = pcall(function() error("boom") end)
            local r = ll.GetNotecardLineSync("card", 1)
            ossl.Report("got", ok, r)
            """, tag: 5);

        Assert.Equal(StepStatus.Preempted, s.Step().Status);
        rig.Results[5] = new[] { Value.Str("line one") };
        StepResult done = s.Step();

        Assert.Equal(StepStatus.Ok, done.Status);
        Assert.Equal(new[] { "str:got false str:line one" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void ADelayingCallAfterAnErrorCaughtInALuaFunctionLetsTheScriptGoOn()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            local ok, e = pcall(function() error("boom") end)
            ossl.Slow(0.2)
            ossl.Report("after", ok)
            """, tag: 6);

        StepResult last = default;
        for (int i = 0; i < 20 && rig.Kit.Snapshot().Length == 0; i++) last = s.Step();

        Assert.NotEqual(StepStatus.Fault, last.Status);
        Assert.Equal(new[] { "str:after false" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void ACallAfterAnErrorCaughtOnTheNativeErrorFunctionReturnsTheAnswer()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            local ok, e = pcall(error, "boom")
            local r = ll.GetNotecardLineSync("card", 1)
            ossl.Report("got", ok, r)
            """, tag: 7);

        Assert.Equal(StepStatus.Preempted, s.Step().Status);
        rig.Results[7] = new[] { Value.Str("line one") };

        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Equal(new[] { "str:got false str:line one" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void AHandlerThatCatchesAnErrorFinishesAndTheScriptTakesTheNextEvent()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function(n)
                local ok = pcall(function() error("boom") end)
                ossl.Report("handled", n, ok)
            end)
            """, tag: 8);
        s.RunToIdle();

        StepResult first = s.Dispatch("touch_start", new[] { Value.Int(1) });
        StepResult second = s.Dispatch("touch_start", new[] { Value.Int(2) });

        Assert.NotEqual(StepStatus.Fault, first.Status);
        Assert.NotEqual(StepStatus.Fault, second.Status);
        Assert.Equal(new[] { "str:handled num:1 false", "str:handled num:2 false" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void AHandlerThatCatchesAnErrorAndThenMakesAPausingCallFinishesAndTakesTheNextEvent()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            LLEvents:on("touch_start", function(n)
                local ok = pcall(function() error("boom") end)
                local r = ll.GetNotecardLineSync("card", n)
                ossl.Report("handled", n, ok, r)
            end)
            """, tag: 9);
        s.RunToIdle();

        Assert.Equal(StepStatus.Preempted, s.Dispatch("touch_start", new[] { Value.Int(1) }).Status);
        rig.Results[9] = new[] { Value.Str("line") };
        StepResult finished = s.Step();

        Assert.Equal(StepStatus.Ok, finished.Status);
        Assert.Equal(new[] { "str:handled num:1 false str:line" }, rig.Kit.Snapshot());
    }
}
