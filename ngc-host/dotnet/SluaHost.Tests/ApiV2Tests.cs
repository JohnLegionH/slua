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
}
