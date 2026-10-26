// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Collections.Concurrent;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>Tests for the additions in API version 3: a host function asking for a delay, and pausing functions the library wraps.</summary>
public class ApiV3Tests
{
    // ---- Shared setup ----

    /// <summary>One VM with the standard function set, a record of the scripts running on it, and a log of pausing calls.</summary>
    sealed class Rig : IDisposable
    {
        public readonly Vm Vm = new(Watchdog.Resident);
        public readonly TestKit Kit = new();
        public readonly ConcurrentDictionary<long, Script> Scripts = new();
        /// <summary>The arguments each pausing call was started with, by script tag.</summary>
        public readonly ConcurrentDictionary<long, string> Started = new();
        /// <summary>What the next take call returns, by script tag.</summary>
        public readonly ConcurrentDictionary<long, Value[]> Results = new();

        public Rig(bool pausing = true, bool slow = true)
        {
            Kit.Register(Vm);
            if (slow)
            {
                Vm.Register("ossl", "Slow", (tag, a) => { Scripts[tag].AddSleep(a[0].N); return null; });
                // An LSL-visible function with a delay of its own: llSay(channel, text) waits 0.2 s here.
                Vm.Register("ll", "Say", (tag, a) => { Kit.Lines.Enqueue("Say " + TestKit.Join(a)); Scripts[tag].AddSleep(0.2); return null; });
            }
            if (pausing)
            {
                Vm.RegisterPausing("ll", "GetNotecardLineSync",
                    (tag, a) => { Started[tag] = TestKit.Join(a); return new[] { Value.Str("ignored") }; },
                    (tag, a) => Results.TryRemove(tag, out var v) ? v : new[] { Value.Str("missing") });
            }
        }

        public Script Run(string source, bool lsl = false, long tag = 1)
        {
            var s = Vm.NewScript(Host.Compile(source, lsl), tag: tag);
            Scripts[tag] = s;
            s.Start();
            return s;
        }

        public Script Fresh(byte[] asset, long tag = 1)
        {
            var s = Vm.NewScript(asset, tag: tag);
            Scripts[tag] = s;
            return s;
        }

        public void Dispose()
        {
            foreach (var s in Scripts.Values) s.Dispose();
            Vm.Dispose();
        }
    }

    /// <summary>Steps until Idle, adding up the sleep every step reported. Returns the last non-idle status too.</summary>
    static (float Sleep, StepResult Last) Drain(Script s, int maxSteps = 1000)
    {
        float sleep = 0;
        StepResult last = default;
        for (int i = 0; i < maxSteps; i++)
        {
            var r = s.Step(0.05);
            sleep += r.SleepServed;
            if (r.Status == StepStatus.Idle) return (sleep, last);
            last = r;
            if (r.Status == StepStatus.Fault) return (sleep, last);
        }
        throw new Xunit.Sdk.XunitException("script did not go idle");
    }

    /// <summary>
    /// LSL scripts take a first step that only enters the default state, so a test steps until the thing it waits for
    /// has happened and returns the result of the step that got it there.
    /// </summary>
    static StepResult StepUntil(Script s, Func<bool> done)
    {
        StepResult r = default;
        for (int i = 0; i < 10 && !done(); i++) r = s.Step(0.05);
        Assert.True(done(), "the script never reached the point the test waits for");
        return r;
    }

    // ---- The version ----

    [Fact]
    public void V3_ApiVersionIsThree() => Assert.Equal(3, Host.ApiVersion());

    [Fact]
    public void V3_VersionTextCarriesTheApiVersion() => Assert.Contains("api=3", Host.Version());

    // ---- A: a host function asks for a delay ----

    [Fact]
    public void A1_SLuaHostFunctionDelayIsReportedToTheScheduler()
    {
        using var rig = new Rig(pausing: false);
        using var s = rig.Run("""
            ossl.Report("a")
            ossl.Slow(0.25)
            ossl.Report("b")
            ossl.Slow(0.5)
            ossl.Report("c")
            """);
        var (sleep, last) = Drain(s);
        Assert.Equal(StepStatus.Ok, last.Status);
        Assert.Equal(0.75f, sleep, 3);
        Assert.Equal(new[] { "str:a", "str:b", "str:c" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void A1_LslHostFunctionDelayIsReportedToTheScheduler()
    {
        using var rig = new Rig(pausing: false);
        using var s = rig.Run("""
            default {
                state_entry() {
                    llSay(0, "one");
                    llSay(0, "two");
                    llOwnerSay("done");
                }
            }
            """, lsl: true);
        var (sleep, last) = Drain(s);
        Assert.Equal(StepStatus.Ok, last.Status);
        Assert.Equal(0.4f, sleep, 3);
        Assert.Equal(new[] { "Say int:0 str:one", "Say int:0 str:two", "OwnerSay str:done" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void A1_AScriptThatAskedForADelayIsPausedBeforeItRunsOn()
    {
        using var rig = new Rig(pausing: false);
        using var s = rig.Run("""
            ossl.Slow(0.25)
            coroutine.yield()
            ossl.Report("after")
            """);
        var first = s.Step();
        Assert.Equal(StepStatus.Preempted, first.Status);
        Assert.Equal(0f, first.SleepServed);
        var second = s.Step();
        Assert.Equal(0.25f, second.SleepServed, 3);
    }

    [Fact]
    public void A1_AddSleepOutsideAHostCallIsRefused()
    {
        using var rig = new Rig(pausing: false);
        using var s = rig.Run("ossl.Report('x')");
        var e = Assert.Throws<SluaException>(() => s.AddSleep(1.0));
        Assert.Equal(-8, e.Code);
    }

    [Fact]
    public void A1_AddSleepRefusesValuesThatAreNotADelay()
    {
        using var rig = new Rig(pausing: false);
        var codes = new List<int>();
        rig.Vm.Register("ossl", "TryBad", (tag, a) =>
        {
            foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.MaxValue })
            {
                try { rig.Scripts[tag].AddSleep(bad); codes.Add(0); }
                catch (SluaException e) { codes.Add(e.Code); }
            }
            return null;
        });
        using var s = rig.Run("ossl.TryBad()");
        var (sleep, last) = Drain(s);
        Assert.Equal(StepStatus.Ok, last.Status);
        Assert.Equal(new[] { -3, -3, -3, -3, -3 }, codes);
        Assert.Equal(0f, sleep);
    }

    [Fact]
    public void A1_AScriptCannotBeGivenADelayFromAnotherScriptsCall()
    {
        using var rig = new Rig(pausing: false);
        int code = 0;
        rig.Vm.Register("ossl", "DelayTheOther", (tag, a) =>
        {
            try { rig.Scripts[2].AddSleep(1.0); }
            catch (SluaException e) { code = e.Code; }
            return null;
        });
        using var other = rig.Run("ossl.Report('other')", tag: 2);
        using var caller = rig.Run("ossl.DelayTheOther()", tag: 1);
        Drain(caller);
        Assert.Equal(-8, code);
        Assert.Equal(0f, Drain(other).Sleep);
    }

    [Fact]
    public void A1_ADelayedSLuaCallSurvivesSaveAndRestore()
    {
        const string source = """
            ossl.Report("before")
            ossl.Slow(0.25)
            coroutine.yield()
            ossl.Report("after")
            """;
        byte[] state;
        using (var rig = new Rig(pausing: false))
        {
            using var s = rig.Run(source);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            Assert.Equal(new[] { "str:before" }, rig.Kit.Snapshot());
            state = s.Serialize();
        }
        using (var rig2 = new Rig(pausing: false))
        {
            using var s2 = rig2.Fresh(Host.Compile(source, false));
            Assert.True(s2.Restore(state));
            var first = s2.Step();
            Assert.Equal(0.25f, first.SleepServed, 3);
            var (_, last) = Drain(s2);
            Assert.Equal(StepStatus.Ok, last.Status);
            Assert.Equal(new[] { "str:after" }, rig2.Kit.Snapshot());
        }
    }

    [Fact]
    public void A1_ADelayedLslCallSurvivesSaveAndRestore()
    {
        const string source = """
            default {
                state_entry() {
                    llOwnerSay("before");
                    llSay(0, "delayed");
                    llOwnerSay("after");
                }
            }
            """;
        byte[] state;
        string[] linesBefore;
        using (var rig = new Rig(pausing: false))
        {
            using var s = rig.Run(source, lsl: true);
            // Stop at the step that reaches the delayed call: the delay is pending and not yet served.
            var first = StepUntil(s, () => rig.Kit.Snapshot().Contains("Say int:0 str:delayed"));
            Assert.Equal(0f, first.SleepServed);
            linesBefore = rig.Kit.Snapshot();
            Assert.Contains("Say int:0 str:delayed", linesBefore);
            state = s.Serialize();
        }
        using (var rig2 = new Rig(pausing: false))
        {
            using var s2 = rig2.Fresh(Host.Compile(source, true));
            Assert.True(s2.Restore(state));
            var (sleep, _) = Drain(s2);
            Assert.Equal(0.2f, sleep, 3);
            // Whatever ran before the save does not run again; every line appears exactly once.
            Assert.Equal(
                new[] { "OwnerSay str:before", "Say int:0 str:delayed", "OwnerSay str:after" },
                linesBefore.Concat(rig2.Kit.Snapshot()).ToArray());
        }
    }

    // ---- B: pausing functions the library wraps ----

    [Fact]
    public void B1_SLuaCallToAPausingFunctionPausesTheScriptAndReturnsTheHostsAnswer()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            local r1 = ll.GetNotecardLineSync("card", 3)
            ossl.Report("got", r1)
            local ok, r2 = pcall(ll.GetNotecardLineSync, "card", 4)
            ossl.Report("got", ok, r2)
            """, tag: 5);

        Assert.Equal(StepStatus.Preempted, s.Step().Status);
        Assert.Equal("str:card num:3", rig.Started[5]);
        Assert.Empty(rig.Kit.Snapshot());

        rig.Results[5] = new[] { Value.Str("line three") };
        Assert.Equal(StepStatus.Preempted, s.Step().Status); // the second call, inside pcall
        Assert.Equal("str:card num:4", rig.Started[5]);
        Assert.Equal(new[] { "str:got str:line three" }, rig.Kit.Snapshot());

        rig.Results[5] = new[] { Value.Str("line four") };
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Equal(new[] { "str:got str:line three", "str:got true str:line four" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void B1_APausingFunctionCanReturnSeveralValues()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            local a, b, c = ll.GetNotecardLineSync("card", 1)
            ossl.Report(a, b, c)
            """, tag: 5);
        Assert.Equal(StepStatus.Preempted, s.Step().Status);
        rig.Results[5] = new[] { Value.Int(1), Value.Str("two"), Value.Key("12345678-9abc-def0-1234-56789abcdef0") };
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Equal(new[] { "num:1 str:two key:12345678-9abc-def0-1234-56789abcdef0" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void B2_LslCallToAPausingFunctionPausesTheScriptAndReturnsTheHostsAnswer()
    {
        using var rig = new Rig();
        using var s = rig.Run("""
            default {
                state_entry() {
                    string a = llGetNotecardLineSync("card", 7);
                    llOwnerSay(a);
                    llOwnerSay(llGetNotecardLineSync("card", 8));
                }
            }
            """, lsl: true, tag: 6);

        Assert.Equal(StepStatus.Preempted, StepUntil(s, () => rig.Started.ContainsKey(6)).Status);
        Assert.Equal("str:card int:7", rig.Started[6]);
        Assert.Empty(rig.Kit.Snapshot());

        rig.Results[6] = new[] { Value.Str("line seven") };
        Assert.Equal(StepStatus.Preempted, s.Step().Status);
        Assert.Equal("str:card int:8", rig.Started[6]);

        rig.Results[6] = new[] { Value.Str("line eight") };
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Equal(new[] { "OwnerSay str:line seven", "OwnerSay str:line eight" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void B3_SLuaScriptWaitingInAPausingCallSurvivesSaveAndRestoreInAFreshVm()
    {
        const string source = """
            ossl.Report("before")
            local r = ll.GetNotecardLineSync("card", 3)
            ossl.Report("got", r)
            """;
        byte[] state;
        using (var rig = new Rig())
        {
            using var s = rig.Run(source, tag: 5);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            Assert.Equal(new[] { "str:before" }, rig.Kit.Snapshot());
            state = s.Serialize();
        }
        using (var rig2 = new Rig())
        {
            using var s2 = rig2.Fresh(Host.Compile(source, false), tag: 5);
            Assert.True(s2.Restore(state));
            rig2.Results[5] = new[] { Value.Str("late answer") };
            Assert.Equal(StepStatus.Ok, s2.Step().Status);
            Assert.Equal(new[] { "str:got str:late answer" }, rig2.Kit.Snapshot());
        }
    }

    [Fact]
    public void B3_LslScriptWaitingInAPausingCallSurvivesSaveAndRestoreInAFreshVm()
    {
        const string source = """
            default {
                state_entry() {
                    llOwnerSay("before");
                    string r = llGetNotecardLineSync("card", 3);
                    llOwnerSay(r);
                }
            }
            """;
        byte[] state;
        using (var rig = new Rig())
        {
            using var s = rig.Run(source, lsl: true, tag: 6);
            Assert.Equal(StepStatus.Preempted, StepUntil(s, () => rig.Started.ContainsKey(6)).Status);
            Assert.Equal(new[] { "OwnerSay str:before" }, rig.Kit.Snapshot());
            state = s.Serialize();
        }
        using (var rig2 = new Rig())
        {
            using var s2 = rig2.Fresh(Host.Compile(source, true), tag: 6);
            Assert.True(s2.Restore(state));
            rig2.Results[6] = new[] { Value.Str("late answer") };
            Assert.Equal(StepStatus.Ok, s2.Step().Status);
            Assert.Equal(new[] { "OwnerSay str:late answer" }, rig2.Kit.Snapshot());
        }
    }

    [Fact]
    public void B3_StateSavedWithAPausingFunctionIsRefusedByAVmWithoutIt()
    {
        const string source = """
            local r = ll.GetNotecardLineSync("card", 3)
            ossl.Report("got", r)
            """;
        byte[] state;
        using (var rig = new Rig())
        {
            using var s = rig.Run(source);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            state = s.Serialize();
        }
        using (var rig2 = new Rig(pausing: false))
        {
            using var s2 = rig2.Fresh(Host.Compile(source, false));
            Assert.False(s2.Restore(state));
        }
    }

    [Fact]
    public void B3_AFunctionBoundAsPlainIsNotTheSameBindingAsAPausingOne()
    {
        const string source = """
            local r = ll.GetNotecardLineSync("card", 3)
            ossl.Report("got", r)
            """;
        byte[] state;
        using (var rig = new Rig())
        {
            using var s = rig.Run(source);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            state = s.Serialize();
        }
        using (var rig2 = new Rig(pausing: false))
        {
            rig2.Vm.Register("ll", "GetNotecardLineSync", (_, _) => new[] { Value.Str("plain") });
            using var s2 = rig2.Fresh(Host.Compile(source, false));
            Assert.False(s2.Restore(state));
        }
    }

    [Fact]
    public void B4_AnErrorFromTheTakeCallIsAScriptErrorTheScriptCanCatch()
    {
        using var rig = new Rig(pausing: false);
        rig.Vm.RegisterPausing("ll", "GetNotecardLineSync",
            (tag, a) => null,
            (tag, a) => throw new InvalidOperationException("answer lost"));
        using var s = rig.Run("""
            local ok, err = pcall(ll.GetNotecardLineSync, "card", 3)
            ossl.Report(ok, err)
            """);
        Assert.Equal(StepStatus.Preempted, s.Step().Status);
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        var line = Assert.Single(rig.Kit.Snapshot());
        Assert.StartsWith("false ", line, StringComparison.Ordinal);
        Assert.Contains("answer lost", line);
    }

    [Fact]
    public void B4_AnErrorFromTheStartCallRaisesBeforeTheScriptPauses()
    {
        using var rig = new Rig(pausing: false);
        rig.Vm.RegisterPausing("ll", "GetNotecardLineSync",
            (tag, a) => throw new InvalidOperationException("cannot start"),
            (tag, a) => null);
        using var s = rig.Run("""
            local ok, err = pcall(ll.GetNotecardLineSync, "card", 3)
            ossl.Report(ok, err)
            """);
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Contains("cannot start", Assert.Single(rig.Kit.Snapshot()));
    }

    [Fact]
    public void B4_ARequestToYieldFromAPausingCallIsAScriptError()
    {
        using var rig = new Rig(pausing: false);
        rig.Vm.RegisterPausing("ll", "GetNotecardLineSync",
            (tag, a) => Host.Yield,
            (tag, a) => null);
        using var s = rig.Run("""
            local ok, err = pcall(ll.GetNotecardLineSync, "card", 3)
            ossl.Report(ok, err)
            """);
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Contains("yield", Assert.Single(rig.Kit.Snapshot()));
    }

    [Fact]
    public void B5_PausingFunctionsCannotBeRegisteredAfterTheFirstImage()
    {
        using var rig = new Rig();
        using var s = rig.Run("ossl.Report('x')");
        var e = Assert.Throws<SluaException>(() => rig.Vm.RegisterPausing("ll", "Other", (_, _) => null, (_, _) => null));
        Assert.Equal(-8, e.Code);
    }

    [Fact]
    public void B5_AScriptThatDoesNotCallThePausingFunctionIsNotAffectedByItsPresence()
    {
        using var rig = new Rig();
        using var s = rig.Run("ossl.Report(ossl.Add(2, 3))");
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Equal(new[] { "num:5" }, rig.Kit.Snapshot());
    }

    [Fact]
    public void B6_AScriptWaitingInAPlainHostFunctionYieldCanAlsoBeSavedAndRestored()
    {
        // Recorded behaviour of version 2, not new: the pausing function is the form to use, but the plain yield saves too.
        const string source = """
            ossl.Report("before")
            ossl.StartAsyncYield(5)
            local r = ossl.TakeResult()
            ossl.Report("got", r)
            """;
        byte[] state;
        using (var rig = new Rig(pausing: false))
        {
            using var s = rig.Run(source, tag: 7);
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            state = s.Serialize();
        }
        using (var rig2 = new Rig(pausing: false))
        {
            using var s2 = rig2.Fresh(Host.Compile(source, false), tag: 7);
            Assert.True(s2.Restore(state));
            rig2.Kit.Results[7] = Value.Int(9);
            Assert.Equal(StepStatus.Ok, s2.Step().Status);
            Assert.Equal(new[] { "str:got num:9" }, rig2.Kit.Snapshot());
        }
    }
}
