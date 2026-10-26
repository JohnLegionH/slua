// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Collections.Concurrent;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// One VM with the standard function set plus the delayed calls the API 4 tests use, and a record of
/// the scripts running on it so a host function can find its own script by tag.
/// </summary>
sealed class V4Rig : IDisposable
{
    public readonly Vm Vm = new(Watchdog.Resident);
    public readonly TestKit Kit = new();
    public readonly ConcurrentDictionary<long, Script> Scripts = new();

    public V4Rig()
    {
        Kit.Register(Vm);
        // Delays with no result.
        Vm.Register("ossl", "Slow", (tag, a) => { Scripts[tag].AddSleep(a[0].N); return null; });
        Vm.Register("ll", "Sleep", (tag, a) => { Scripts[tag].AddSleep(a[0].N); return null; });
        // A delay on a call that gives a value back.
        Vm.Register("ossl", "SlowEcho", (tag, a) => { Scripts[tag].AddSleep(a[0].N); return new[] { a[1] }; });
        // A call that asks for the delay and yields by itself.
        Vm.Register("ossl", "SlowYield", (tag, a) => { Scripts[tag].AddSleep(a[0].N); return Host.Yield; });
        // An LSL-visible call with a delay of its own: llSay waits 0.2 s here.
        Vm.Register("ll", "Say", (tag, a) => { Kit.Lines.Enqueue("Say " + TestKit.Join(a)); Scripts[tag].AddSleep(0.2); return null; });
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

    /// <summary>Steps until the lines the host recorded satisfy <paramref name="done"/>; returns every step's result, in order.</summary>
    public List<StepResult> StepUntilLines(Script s, Func<string[], bool> done, double quanta = 0.05, int maxSteps = 20)
    {
        var results = new List<StepResult>();
        for (int i = 0; i < maxSteps && !done(Kit.Snapshot()); i++) results.Add(s.Step(quanta));
        Assert.True(done(Kit.Snapshot()), "the script never reached the point the test waits for");
        return results;
    }

    public void Dispose()
    {
        foreach (var s in Scripts.Values) s.Dispose();
        Vm.Dispose();
    }
}
