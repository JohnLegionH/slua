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

    // ---- The version ----

    [Fact]
    public void V3_ApiVersionIsThree() => Assert.Equal(3, Host.ApiVersion());

    [Fact]
    public void V3_VersionTextCarriesTheApiVersion() => Assert.Contains("api=3", Host.Version());

}
