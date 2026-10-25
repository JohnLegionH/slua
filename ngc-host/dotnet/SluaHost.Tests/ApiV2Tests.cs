// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>Tests for the native layer's additions in API version 2: versioning, memory limits, the os*/iw*/bot* refusal, key/rotation/list values, event arguments and shared images.</summary>
public class ApiV2Tests
{
    const string KeyA = "12345678-9abc-def0-1234-56789abcdef0";
    const string KeyB = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    static (Vm vm, TestKit kit) NewVm(Watchdog w = Watchdog.Resident)
    {
        var kit = new TestKit();
        var vm = new Vm(w);
        kit.Register(vm);
        return (vm, kit);
    }

    static Script Run(Vm vm, string source, bool lsl = false, long tag = 1)
    {
        var s = vm.NewScript(Host.Compile(source, lsl), tag: tag);
        s.Start();
        return s;
    }

    // ---- The version function ----

    [Fact]
    public void B6_ApiVersionIsTwo()
    {
        Assert.Equal(2, Host.ApiVersion());
    }

    [Fact]
    public unsafe void B6_ManagedStructLayoutMatchesTheNativeOne()
    {
        int nativeValue = Host.NativeStructSize(0), nativeStep = Host.NativeStructSize(1);
        Assert.Equal(sizeof(Native.SluaValue), nativeValue);
        Assert.Equal(sizeof(Native.StepResultN), nativeStep);
        if (IntPtr.Size == 8) Assert.Equal(56, nativeValue);
    }

    // ---- Memory limits ----

    [Fact]
    public void B4_LimitCanBeLoweredAndTheLoweredLimitIsEnforced()
    {
        var (vm, _) = NewVm();
        using var _v = vm;
        using var s = Run(vm, """
            local t = {}
            for i = 1, 100000 do t[i] = { i } end
            """);
        Assert.Equal(128 * 1024, s.MemoryLimit);
        s.SetMemoryLimit(48 * 1024);
        Assert.Equal(48 * 1024, s.MemoryLimit);
        Assert.Equal(StepStatus.Fault, s.RunToIdle(0.05).Status);
        Assert.Equal(FaultKind.OutOfMemory, s.Fault().Kind);
    }

    [Fact]
    public void B4_LimitAboveTheCeilingIsRefusedAndTheLimitStays()
    {
        var (vm, _) = NewVm();
        using var _v = vm;
        using var s = Run(vm, "ossl.Nop()");
        var e = Assert.Throws<SluaException>(() => s.SetMemoryLimit(128 * 1024 + 1));
        Assert.Equal(-9, e.Code);
        Assert.Equal(128 * 1024, s.MemoryLimit);
        s.SetMemoryLimit(64 * 1024);
        e = Assert.Throws<SluaException>(() => s.SetMemoryLimit(1024 * 1024));
        Assert.Equal(-9, e.Code);
        Assert.Equal(64 * 1024, s.MemoryLimit);
    }

    [Fact]
    public void B4_LimitBelowWhatTheScriptUsesIsRefused()
    {
        var (vm, _) = NewVm();
        using var _v = vm;
        using var s = Run(vm, "local t = {} for i = 1, 100 do t[i] = { i } end coroutine.yield()");
        s.Step();
        Assert.True(s.UsedMemory > 1024);
        var e = Assert.Throws<SluaException>(() => s.SetMemoryLimit(512));
        Assert.Equal(-9, e.Code);
    }

    [Fact]
    public void B4_NewScriptWithALimitAboveTheCeilingIsRefused()
    {
        var (vm, _) = NewVm();
        using var _v = vm;
        byte[] asset = Host.Compile("ossl.Nop()", false);
        var e = Assert.Throws<SluaException>(() => vm.NewScript(asset, 256 * 1024));
        Assert.Equal(-9, e.Code);
        using var ok = vm.NewScript(asset, 32 * 1024);
        Assert.Equal(32 * 1024, ok.MemoryLimit);
        using var lsl = vm.NewScript(Host.Compile("default { state_entry() { } }", true), 16 * 1024);
        Assert.Equal(16 * 1024, lsl.MemoryLimit);
    }

    // ---- os*, iw* and bot* calls in LSL are refused with a message ----

    [Theory]
    [InlineData("osGetGridName")]
    [InlineData("iwGetAvatarMoney")]
    [InlineData("botCreateBot")]
    public void B5_LslCallToAnOsIwOrBotFunctionIsRefusedAndNamed(string fn)
    {
        Host.Init();
        string src = $"default {{ state_entry() {{ llOwnerSay((string){fn}()); }} }}";
        var e = Assert.Throws<SluaException>(() => Host.Compile(src, true));
        Assert.Equal(-10, e.Code);
        Assert.Contains(fn, e.Message);
        Assert.Contains("Phlox", e.Message);
        Assert.Contains("os*, iw* or bot*", e.Message);
    }

    [Fact]
    public void B5_EveryRefusedFunctionIsNamedOnce()
    {
        Host.Init();
        const string src = "default { state_entry() { osFoo(); osFoo(); botBar(); llOwnerSay(\"x\"); } }";
        var e = Assert.Throws<SluaException>(() => Host.Compile(src, true));
        Assert.Equal(-10, e.Code);
        Assert.Contains("osFoo", e.Message);
        Assert.Contains("botBar", e.Message);
        Assert.Equal(1, e.Message.Split("osFoo").Length - 1);
    }

    [Fact]
    public void B5_OtherUndeclaredFunctionsKeepTheOrdinaryCompileError()
    {
        Host.Init();
        var e = Assert.Throws<SluaException>(() => Host.Compile("default { state_entry() { llNoSuchFunction(); } }", true));
        Assert.Equal(-5, e.Code);
        Assert.DoesNotContain("Phlox", e.Message);
    }

    [Fact]
    public void B5_AScriptsOwnFunctionWithAnOsNameStillCompiles()
    {
        Host.Init();
        byte[] asset = Host.Compile("integer osHelper(integer a) { return a + 1; }\ndefault { state_entry() { llOwnerSay((string)osHelper(1)); } }", true);
        Assert.NotEmpty(asset);
    }

    [Fact]
    public void B5_SLuaSourceIsNotAffected()
    {
        Host.Init();
        Assert.NotEmpty(Host.Compile("local x = osGetGridName", false));
    }

    // ---- Key, rotation and list cross the boundary both ways ----

    static void RegisterValueFns(Vm vm, TestKit kit)
    {
        vm.Register("ossl", "MakeValues", (_, _) => new[]
        {
            Value.Key(KeyB), Value.Rot(0.5f, 0.25f, 0.125f, 1f),
            Value.List(Value.Int(7), Value.Num(2.5), Value.Str("s"), Value.Key(KeyA), Value.Vec(1, 2, 3), Value.Rot(1, 2, 3, 4)),
        });
        vm.Register("ll", "GetOwner", (_, _) => new[] { Value.Key(KeyA) });
        vm.Register("ll", "Key2Name", (_, a) => { kit.Lines.Enqueue("Key2Name " + TestKit.Join(a)); return new[] { Value.Str("Example Name") }; });
        vm.Register("ll", "Euler2Rot", (_, a) => { kit.Lines.Enqueue("Euler2Rot " + TestKit.Join(a)); return new[] { Value.Rot(0, 0, 0.5f, 0.75f) }; });
        vm.Register("ll", "Rot2Euler", (_, a) => { kit.Lines.Enqueue("Rot2Euler " + TestKit.Join(a)); return new[] { Value.Vec(1, 2, 3) }; });
        vm.Register("ll", "ListSort", (_, a) => { kit.Lines.Enqueue("ListSort " + TestKit.Join(a)); return new[] { a[0] }; });
        vm.Register("ll", "DumpList2String", (_, a) => { kit.Lines.Enqueue("DumpList2String " + TestKit.Join(a)); return new[] { Value.Str("dumped") }; });
    }

    [Fact]
    public void B3_SLuaKeyRotationAndListGoOutAndComeBack()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        RegisterValueFns(vm, kit);
        using var s = Run(vm, $$"""
            local k, r, l = ossl.Echo(uuid("{{KeyA}}"), quaternion(1, 2, 3, 4), { 1, 2.5, "s", vector(1, 2, 3) })
            ossl.Report(typeof(k), typeof(r), typeof(l), #l)
            ossl.Report(k, r, l)
            local mk, mr, ml = ossl.MakeValues()
            ossl.Report(typeof(mk), typeof(mr), typeof(ml), #ml)
            ossl.Report(mk, mr, ml)
            """);
        Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        var lines = kit.Snapshot();
        Assert.Equal("str:uuid str:quaternion str:table num:4", lines[0]);
        Assert.Equal($"key:{KeyA} rot:<1,2,3,4> list:[num:1,num:2.5,str:s,vec:<1,2,3>]", lines[1]);
        Assert.Equal("str:uuid str:quaternion str:table num:6", lines[2]);
        Assert.Equal($"key:{KeyB} rot:<0.5,0.25,0.125,1> list:[num:7,num:2.5,str:s,key:{KeyA},vec:<1,2,3>,rot:<1,2,3,4>]", lines[3]);
    }

    [Fact]
    public void B3_ANestedListIsRefusedAsAScriptError()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var s = Run(vm, "local ok, err = pcall(ossl.Echo, { 1, { 2 } })\nossl.Report(ok)");
        Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        Assert.Equal(new[] { "false" }, kit.Snapshot());
    }

    [Fact]
    public void B3_LslKeyRotationAndListGoOutAndComeBack()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        RegisterValueFns(vm, kit);
        using var s = Run(vm, """
            default {
                state_entry() {
                    key o = llGetOwner();
                    llOwnerSay(llKey2Name(o));
                    rotation q = llEuler2Rot(<0.0, 0.0, 0.0>);
                    llOwnerSay((string)llRot2Euler(<1.0, 2.0, 3.0, 4.0>));
                    list l = llListSort([3, 1.5, "a", o, <1.0, 2.0, 3.0>, <1.0, 2.0, 3.0, 4.0>], 1, TRUE);
                    llOwnerSay(llDumpList2String(l, ","));
                    llOwnerSay((string)q);
                }
            }
            """, lsl: true);
        Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        var lines = kit.Snapshot();
        Assert.Equal($"Key2Name key:{KeyA}", lines[0]);
        Assert.Equal("OwnerSay str:Example Name", lines[1]);
        Assert.Equal("Euler2Rot vec:<0,0,0>", lines[2]);
        Assert.Equal("Rot2Euler rot:<1,2,3,4>", lines[3]);
        Assert.Equal("OwnerSay str:<1.00000, 2.00000, 3.00000>", lines[4]);
        Assert.Equal($"ListSort list:[int:3,num:1.5,str:a,key:{KeyA},vec:<1,2,3>,rot:<1,2,3,4>] int:1 int:1", lines[5]);
        Assert.Equal($"DumpList2String list:[int:3,num:1.5,str:a,key:{KeyA},vec:<1,2,3>,rot:<1,2,3,4>] str:,", lines[6]);
        Assert.Equal("OwnerSay str:dumped", lines[7]);
        Assert.Equal("OwnerSay str:<0.00000, 0.00000, 0.50000, 0.75000>", lines[8]);
    }

    // ---- Event arguments ----

    const string SLuaHandlers = """
        LLEvents:on("link_message", function(...) ossl.Report("link_message", ...) end)
        LLEvents:on("dataserver", function(query, data) ossl.Report("dataserver", data) end)
        """;

    [Fact]
    public void B2_SLuaEventHandlerReceivesEveryArgumentType()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var s = Run(vm, SLuaHandlers);
        Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        var r = s.Dispatch("link_message", new[]
        {
            Value.Int(-3), Value.Num(1.5), Value.Str("héllo"), Value.Key(KeyA), Value.Vec(1, 2, 3), Value.Rot(1, 2, 3, 4),
            Value.List(Value.Int(1), Value.Str("x")),
        });
        Assert.Equal(StepStatus.Ok, r.Status);
        Assert.Equal(new[] { $"str:link_message num:-3 num:1.5 str:héllo key:{KeyA} vec:<1,2,3> rot:<1,2,3,4> list:[num:1,str:x]" }, kit.Snapshot());
    }

    [Fact]
    public void B2_TwoDispatchesInARow()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var s = Run(vm, SLuaHandlers);
        s.RunToIdle();
        Assert.Equal(StepStatus.Ok, s.Dispatch("dataserver", new[] { Value.Key(KeyA), Value.Str("one") }).Status);
        Assert.Equal(StepStatus.Ok, s.Dispatch("dataserver", new[] { Value.Key(KeyA), Value.Str("two") }).Status);
        Assert.Equal(new[] { "str:dataserver str:one", "str:dataserver str:two" }, kit.Snapshot());
    }

    [Fact]
    public void B2_EventTheScriptDoesNotHandleIsNotRun()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var s = Run(vm, SLuaHandlers);
        s.RunToIdle();
        Assert.Equal(StepStatus.NotRun, s.Dispatch("timer", ReadOnlySpan<Value>.Empty).Status);
        Assert.Empty(kit.Snapshot());
    }

    [Fact]
    public void B2_UnknownEventNameIsAnError()
    {
        Host.Init();
        Assert.Throws<SluaException>(() => Host.EventIndex("no_such_event"));
        Assert.True(Host.EventIndex("touch_start") > 0);
    }

    [Fact]
    public void B2_DispatchWhileAHandlerIsInFlightIsRefusedThenWorksAfterItFinishes()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var s = Run(vm, """
            LLEvents:on("dataserver", function(query, data)
                coroutine.yield()
                ossl.Report("dataserver done", data)
            end)
            LLEvents:on("timer", function() ossl.Report("timer") end)
            """);
        s.RunToIdle();
        Assert.Equal(StepStatus.Preempted, s.Dispatch("dataserver", new[] { Value.Key(KeyA), Value.Str("four") }).Status);
        var e = Assert.Throws<SluaException>(() => s.Dispatch("timer", ReadOnlySpan<Value>.Empty));
        Assert.Equal(-8, e.Code);
        Assert.Equal(StepStatus.Ok, s.Step().Status);
        Assert.Equal(StepStatus.Ok, s.Dispatch("timer", ReadOnlySpan<Value>.Empty).Status);
        Assert.Equal(new[] { "str:dataserver done str:four", "str:timer" }, kit.Snapshot());
    }

    [Fact]
    public void B2_ALongHandlerIsSteppedToTheEnd()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var s = Run(vm, """
            LLEvents:on("dataserver", function(query, data)
                local sum = 0
                for i = 1, 3000000 do sum += i end
                ossl.Report("sum", sum)
            end)
            """);
        s.RunToIdle();
        var r = s.Dispatch("dataserver", new[] { Value.Key(KeyA), Value.Str("x") }, 0.0002);
        Assert.Equal(StepStatus.Preempted, r.Status);
        Assert.Equal(StepStatus.Ok, s.RunToIdle(0.05).Status);
        Assert.Equal(new[] { "str:sum num:4500001500000" }, kit.Snapshot());
    }

    const string LslEvents = """
        default {
            state_entry() { llOwnerSay("entry"); }
            link_message(integer sender, integer num, string str, key id) { llOwnerSay("lm " + (string)sender + " " + (string)num + " " + str + " " + (string)id); }
            at_target(integer tnum, vector targetpos, vector ourpos) { llSay(tnum, (string)targetpos); }
            at_rot_target(integer tnum, rotation targetrot, rotation ourrot) { llSay(tnum, (string)targetrot); }
            path_update(integer type, list reserved) { llOwnerSay(llDumpList2String(reserved, ",")); }
            dataserver(key query, string data) { state second; }
        }
        state second {
            state_entry() { llOwnerSay("second"); }
            touch_start(integer n) { llOwnerSay("touched " + (string)n); }
        }
        """;

    [Fact]
    public void B2_LslEventHandlersReceiveTheirArguments()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        RegisterValueFns(vm, kit);
        using var s = Run(vm, LslEvents, lsl: true);
        Assert.Equal(StepStatus.Ok, s.RunToIdle().Status);
        Assert.Equal(new[] { "OwnerSay str:entry" }, kit.Snapshot());

        Assert.Equal(StepStatus.Ok, s.Dispatch("link_message", new[] { Value.Int(1), Value.Int(2), Value.Str("hi"), Value.Key(KeyA) }).Status);
        Assert.Equal(StepStatus.Ok, s.Dispatch("at_target", new[] { Value.Int(9), Value.Vec(1, 2, 3), Value.Vec(4, 5, 6) }).Status);
        Assert.Equal(StepStatus.Ok, s.Dispatch("at_rot_target", new[] { Value.Int(8), Value.Rot(0, 0, 0, 1), Value.Rot(0, 0, 0, 1) }).Status);
        Assert.Equal(StepStatus.Ok, s.Dispatch("path_update", new[] { Value.Int(3), Value.List(Value.Int(5), Value.Str("a")) }).Status);
        var lines = kit.Snapshot();
        Assert.Equal($"OwnerSay str:lm 1 2 hi {KeyA}", lines[1]);
        Assert.Equal("Say int:9 str:<1.00000, 2.00000, 3.00000>", lines[2]);
        Assert.Equal("Say int:8 str:<0.00000, 0.00000, 0.00000, 1.00000>", lines[3]);
        Assert.Equal("DumpList2String list:[int:5,str:a] str:,", lines[4]);
    }

    [Fact]
    public void B2_LslEventThatChangesStateReportsItAndTheNextDispatchWaitsForTheStep()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        RegisterValueFns(vm, kit);
        using var s = Run(vm, LslEvents, lsl: true);
        s.RunToIdle();
        var r = s.Dispatch("dataserver", new[] { Value.Key(KeyA), Value.Str("d") });
        Assert.Equal(StepStatus.StateChange, r.Status);
        Assert.Equal(1, r.NewState);
        var e = Assert.Throws<SluaException>(() => s.Dispatch("touch_start", new[] { Value.Int(1) }));
        Assert.Equal(-8, e.Code);
        s.RunToIdle();
        Assert.Equal(StepStatus.Ok, s.Dispatch("touch_start", new[] { Value.Int(2) }).Status);
        // The first state no longer handles link_message.
        Assert.Equal(StepStatus.NotRun, s.Dispatch("link_message", new[] { Value.Int(1), Value.Int(2), Value.Str("x"), Value.Key(KeyA) }).Status);
        var lines = kit.Snapshot();
        Assert.Equal("OwnerSay str:second", lines[1]);
        Assert.Equal("OwnerSay str:touched 2", lines[2]);
    }

    // ---- One image, many scripts ----

    const string Counter = """
        local n = 0
        while true do
            n += 1
            ossl.Report(ossl.Tag(), n)
            coroutine.yield()
        end
        """;

    [Fact]
    public void B1_ManyScriptsFromOneImageKeepTheirOwnState()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        vm.Register("ossl", "Tag", (tag, _) => new[] { Value.Int(tag) });
        using var image = vm.NewImage(Host.Compile(Counter, false));
        using var a = image.NewScript(tag: 10);
        using var b = image.NewScript(tag: 20);
        using var c = image.NewScript(tag: 30);
        a.Start(); b.Start(); c.Start();
        a.Step(); a.Step(); b.Step(); c.Step(); a.Step();
        Assert.Equal(new[] { "num:10 num:1", "num:10 num:2", "num:20 num:1", "num:30 num:1", "num:10 num:3" }, kit.Snapshot());
    }

    [Fact]
    public void B1_ScriptsKeepWorkingAfterTheImageHandleIsDisposed()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        Script a, b;
        using (var image = vm.NewImage(Host.Compile("ossl.Report('ran')", false)))
        {
            a = image.NewScript(tag: 1);
            b = image.NewScript(tag: 2);
        }
        a.Start(); b.Start();
        Assert.Equal(StepStatus.Ok, a.RunToIdle().Status);
        Assert.Equal(StepStatus.Ok, b.RunToIdle().Status);
        Assert.Equal(2, kit.Snapshot().Length);
        a.Dispose(); b.Dispose();
    }

    [Fact]
    public unsafe void B1_AVmWithALiveImageOrScriptCannotBeFreed()
    {
        var (vm, _) = NewVm();
        var image = vm.NewImage(Host.Compile("ossl.Nop()", false));
        Assert.Equal(-8, Native.slua_vm_free(vm.Handle));
        var s = image.NewScript();
        image.Dispose();
        Assert.Equal(-8, Native.slua_vm_free(vm.Handle));
        s.Dispose();
        vm.Dispose();
    }

    [Fact]
    public void B1_LslScriptsShareAnImageToo()
    {
        var (vm, kit) = NewVm();
        using var _v = vm;
        using var image = vm.NewImage(Host.Compile("integer n;\ndefault { state_entry() { n += 1; llOwnerSay((string)n); } }", true));
        using var a = image.NewScript(tag: 1);
        using var b = image.NewScript(tag: 2);
        a.Start(); b.Start();
        a.RunToIdle(); b.RunToIdle();
        Assert.Equal(new[] { "OwnerSay str:1", "OwnerSay str:1" }, kit.Snapshot());
        Assert.Equal(128 * 1024, a.MemoryLimit);
    }

    [Fact]
    public void B1_SharedImageCostsLessMemoryPerScriptThanAPrivateOne()
    {
        var kit = new TestKit();
        byte[] largeAsset = Host.Compile(
            "local t = { 1, 2, 3 }\nlocal function f() return t end\nossl.Nop()\nfunction ondone() end\n" +
            string.Concat(Enumerable.Range(0, 40).Select(i => $"local function g{i}(a, b) return a * {i + 1} + b - {i} end\nossl.Nop(g{i}(1, 2))\n")) +
            "coroutine.yield()", false);
        const int n = 300;

        long Measure1(bool shared, out long counted, byte[]? other = null)
        {
            byte[] asset = other ?? largeAsset;
            using var vm = new Vm(Watchdog.Resident);
            kit.Register(vm);
            using var image = shared ? vm.NewImage(asset) : null;
            GC.Collect(); GC.WaitForPendingFinalizers();
            long before = Measure.RssBytes();
            var scripts = new List<Script>();
            counted = 0;
            for (int i = 0; i < n; i++)
            {
                var s = shared ? image!.NewScript(tag: i) : vm.NewScript(asset, tag: i);
                s.Start();
                s.Step();
                counted += s.UsedMemory;
                scripts.Add(s);
            }
            long after = Measure.RssBytes();
            foreach (var s in scripts) s.Dispose();
            counted /= n;
            return (after - before) / n;
        }

        Measure1(false, out _); // warm up allocator
        long privatePer = Measure1(false, out long privateCounted);
        long sharedPer = Measure1(true, out long sharedCounted);
        Measure.Record("b1-memory-per-script", $"private image {privatePer} bytes RSS per script, shared image {sharedPer} bytes (n={n}); counted against the limit: private {privateCounted}, shared {sharedCounted}");
        // The same small script as the earlier per-script measurement, for comparison.
        byte[] small = Host.Compile("""
            local t = { 1, 2, 3 }
            local function f() return t end
            ossl.Nop()
            function ondone() end
            coroutine.yield()
            """, false);
        long smallPrivate = Measure1(false, out _, small), smallShared = Measure1(true, out _, small);
        Measure.Record("b1-memory-per-script-small", $"private image {smallPrivate} bytes RSS per script, shared image {smallShared} bytes (n={n})");
        Assert.True(sharedPer < privatePer, $"shared {sharedPer} B per script is not below private {privatePer} B");
    }
}
