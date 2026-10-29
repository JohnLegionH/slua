// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// A saved script state names the host functions it holds by their place in the global tables ("g/ossl/Add"), the way Second Life's
/// own server restores a script, so a host that has since bound more functions still restores it. A state is refused, with the
/// function's name, only when it holds a function this build no longer binds, or binds in another way (plain or pausing).
/// The "live-v1" files in TestData were written by the 0.3.1 release of the library (header version 1, which records only a hash of the
/// whole function list); they are restored under a function list that differs from the one they were saved with.
/// </summary>
public class SavedStateFunctionSetTests
{
    // The script holds nothing but the Report function between its two halves, which every list below binds.
    const string HoldsNothing = """
        local t = { a = 1, list = { 10, 20 } }
        local function mk() local n = 0; return function() n += 1; return n end end
        local c = mk()
        c(); c()
        ossl.Report("before", t.a, c())
        coroutine.yield()
        t.a += 1
        ossl.Report("after", t.a, c(), t.list[2], ossl.GetGridName())
        """;

    // The script keeps the Add function in a local across the yield, so the saved state holds it.
    const string HoldsAdd = """
        local add = ossl.Add
        ossl.Report("before", add(1, 2))
        coroutine.yield()
        ossl.Report("after", add(40, 2))
        """;

    static readonly string[] HoldsNothingBefore = { "str:before num:1 num:3" };
    static readonly string[] HoldsNothingAfter = { "str:after num:2 num:4 num:20 str:Example Grid" };

    /// <summary>The standard function list, optionally with one more, or without the Add function.</summary>
    static (Vm vm, TestKit kit) NewVm(bool extra = false, bool withoutAdd = false)
    {
        var kit = new TestKit();
        var vm = new Vm(Watchdog.Resident);
        if (!withoutAdd)
            kit.Register(vm, extra);
        else
        {
            vm.Register("ossl", "GetGridName", (_, _) => new[] { Value.Str("Example Grid") });
            vm.Register("ossl", "Report", (tag, a) => { kit.Lines.Enqueue(TestKit.Join(a)); return null; });
            vm.Register("ll", "OwnerSay", (tag, a) => { kit.Lines.Enqueue("OwnerSay " + TestKit.Join(a)); return null; });
        }
        return (vm, kit);
    }

    static byte[] SaveAtTheYield(string source, bool extra = false)
    {
        var (vm, _) = NewVm(extra);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(source, false), tag: 1);
            s.Start();
            Assert.Equal(StepStatus.Preempted, s.Step().Status);
            return s.Serialize();
        }
    }

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    // ---- a function added since the state was saved ----

    [Fact]
    public void AStateSavedBeforeAFunctionWasAddedRestoresAndRunsOn()
    {
        byte[] state = SaveAtTheYield(HoldsNothing);
        var (vm, kit) = NewVm(extra: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsNothing, false), tag: 1);
            Assert.True(s.Restore(state), "the state was refused: " + s.RestoreNote);
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(HoldsNothingAfter, kit.Snapshot()); // the part before the save did not run again
        }
    }

    [Fact]
    public void AStateThatHoldsAFunctionRestoresWhenOtherFunctionsWereAdded()
    {
        byte[] state = SaveAtTheYield(HoldsAdd);
        var (vm, kit) = NewVm(extra: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsAdd, false), tag: 1);
            Assert.True(s.Restore(state), "the state was refused: " + s.RestoreNote);
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(new[] { "str:after num:42" }, kit.Snapshot());
        }
    }

    // ---- a function removed since the state was saved ----

    [Fact]
    public void AStateThatHoldsAFunctionThatIsGoneIsRefusedByNameAndTheScriptStartsFresh()
    {
        byte[] state = SaveAtTheYield(HoldsAdd);
        var (vm, kit) = NewVm(withoutAdd: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsAdd, false), tag: 1);
            Assert.False(s.Restore(state));
            Assert.Contains("ossl.Add", s.RestoreNote);
            Assert.Contains("no longer", s.RestoreNote);
            Assert.Empty(kit.Snapshot()); // nothing of the old state ran
        }
    }

    [Fact]
    public void AStateThatDoesNotHoldTheFunctionThatIsGoneRestores()
    {
        byte[] state = SaveAtTheYield(HoldsNothing);
        var (vm, kit) = NewVm(withoutAdd: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsNothing, false), tag: 1);
            Assert.True(s.Restore(state), "the state was refused: " + s.RestoreNote);
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(HoldsNothingAfter, kit.Snapshot());
        }
    }

    [Fact]
    public void AFunctionBoundAsPlainWhereTheStateSavedAPausingOneIsRefusedByName()
    {
        const string source = """
            local r = ll.GetNotecardLineSync("card", 3)
            ossl.Report("got", r)
            """;
        byte[] state;
        {
            var (vm, _) = NewVm();
            using (vm)
            {
                vm.RegisterPausing("ll", "GetNotecardLineSync", (tag, a) => new[] { Value.Str("ignored") }, (tag, a) => new[] { Value.Str("late") });
                using var s = vm.NewScript(Host.Compile(source, false), tag: 1);
                s.Start();
                Assert.Equal(StepStatus.Preempted, s.Step().Status);
                state = s.Serialize();
            }
        }
        var (vm2, _) = NewVm();
        using (vm2)
        {
            vm2.Register("ll", "GetNotecardLineSync", (_, _) => new[] { Value.Str("plain") });
            using var s2 = vm2.NewScript(Host.Compile(source, false), tag: 1);
            Assert.False(s2.Restore(state));
            Assert.Contains("ll.GetNotecardLineSync", s2.RestoreNote);
            Assert.Contains("pausing", s2.RestoreNote);
        }
    }

    // ---- states written by the 0.3.1 release of the library ----

    [Fact]
    public void AStateTheV031LibrarySavedRestoresUnderALongerFunctionList()
    {
        byte[] state = File.ReadAllBytes(Fixture("live-v1-holds-nothing.state"));
        Assert.Equal(1, BitConverter.ToInt32(state, 4)); // header version 1: the 0.3.1 library's layout
        var (vm, kit) = NewVm(extra: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsNothing, false), tag: 1);
            Assert.True(s.Restore(state), "the state was refused: " + s.RestoreNote);
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(HoldsNothingAfter, kit.Snapshot());
        }
    }

    [Fact]
    public void AStateTheV031LibrarySavedWithTheSameListStillRestores()
    {
        byte[] state = File.ReadAllBytes(Fixture("live-v1-holds-nothing.state"));
        var (vm, kit) = NewVm();
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsNothing, false), tag: 1);
            Assert.True(s.Restore(state), "the state was refused: " + s.RestoreNote);
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(HoldsNothingAfter, kit.Snapshot());
        }
    }

    [Fact]
    public void AStateTheV031LibrarySavedThatHoldsAFunctionRestoresUnderALongerFunctionList()
    {
        byte[] state = File.ReadAllBytes(Fixture("live-v1-holds-add.state"));
        var (vm, kit) = NewVm(extra: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsAdd, false), tag: 1);
            Assert.True(s.Restore(state), "the state was refused: " + s.RestoreNote);
            Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
            Assert.Equal(new[] { "str:after num:42" }, kit.Snapshot());
        }
    }

    [Fact]
    public void AStateTheV031LibrarySavedThatHoldsAFunctionThatIsGoneIsRefusedAndSaysWhy()
    {
        byte[] state = File.ReadAllBytes(Fixture("live-v1-holds-add.state"));
        var (vm, kit) = NewVm(withoutAdd: true);
        using (vm)
        {
            using var s = vm.NewScript(Host.Compile(HoldsAdd, false), tag: 1);
            Assert.False(s.Restore(state));
            Assert.NotEmpty(s.RestoreNote); // the version 1 layout does not name the function; the note says the list had changed
            Assert.Contains("function", s.RestoreNote);
            Assert.Empty(kit.Snapshot());
        }
    }

    [Fact]
    public void ANewStateIsReadByTheV031LibraryAsAnUnknownVersion()
    {
        byte[] state = SaveAtTheYield(HoldsNothing);
        Assert.Equal("SLSH", System.Text.Encoding.ASCII.GetString(state, 0, 4));
        Assert.Equal(2, BitConverter.ToInt32(state, 4)); // a library that reads only version 1 starts the script fresh, which is a clean refusal
    }
}
