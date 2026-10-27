// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Text;
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// API 5: the host gives the runtime a list of the functions it supplies for LSL (os*, iw* and bot* functions) and the LSL compiler
/// accepts calls to them as it accepts ll calls. A call to <c>osName</c>, <c>iwName</c> or <c>botName</c> runs the host function
/// registered as <c>ossl.Name</c>, <c>iw.Name</c> or <c>bot.Name</c>, the same one an SLua script calls by that name; <c>ll</c> and
/// <c>llcompat</c> stay the runtime's own. A name that is neither in the list nor an ll function fails to compile with its line. A
/// host that never gives a list gets what API 4 gave, and a library at API 4 is refused by version.
/// The list is process-wide, so these tests share the collection of the other process-wide settings (the engine identity).
/// </summary>
[Collection("engine identity")]
public sealed class ApiV5HostFunctionsTests : IDisposable
{
    const string AddOne = "integer osAddOne( integer n )";

    public void Dispose()
    {
        Host.SetHostFunctions(null);
        Host.SetEngineIdentity(null);
    }

    static long Int(Value v) => v.Type == ValueType.Number ? (long)v.N : v.I;

    static V4Rig RigWithHostFunctions()
    {
        var rig = new V4Rig();
        rig.Vm.Register("ossl", "AddOne", (_, a) => new[] { Value.Int(Int(a[0]) + 1) });
        rig.Vm.Register("iw", "Twice", (_, a) => new[] { Value.Int(Int(a[0]) * 2) });
        rig.Vm.Register("bot", "Hello", (_, a) => new[] { Value.Str("hello " + a[0].S) });
        return rig;
    }

    static string CompileError(string source, bool lsl, out int code)
    {
        var e = Assert.Throws<SluaException>(() => Host.Compile(source, lsl));
        code = e.Code;
        return e.Message;
    }

    // ---- the version ----

    [Fact]
    public void Version_TheLibraryIsAtApiFiveAndTheStepResultIsStill32Bytes()
    {
        Assert.Equal(5, Host.ApiVersion());
        Assert.Equal(5, Host.RequiredApiVersion);
        Assert.Equal(32, Host.NativeStructSize(1));
    }

    // ---- a call to a host function ----

    [Fact]
    public void Lsl_ACallToAnOsFunctionInTheHostsListCompilesAndRunsTheHostFunction()
    {
        Host.SetHostFunctions(AddOne);
        using var rig = RigWithHostFunctions();
        using var s = rig.Run("default { state_entry() { llOwnerSay((string)osAddOne(41)); } }", lsl: true);

        rig.StepUntilLines(s, l => l.Contains("OwnerSay str:42"));
    }

    [Fact]
    public void Lsl_TheCallsGoToTheModulesOssl_Iw_AndBotAndLlStaysLl()
    {
        Host.SetHostFunctions("integer osAddOne( integer n )\ninteger iwTwice( integer n )\nstring botHello( string who )");
        using var rig = RigWithHostFunctions();
        using var s = rig.Run("default { state_entry() { llOwnerSay((string)osAddOne(1) + \" \" + (string)iwTwice(21) + \" \" + botHello(\"x\")); } }", lsl: true);

        rig.StepUntilLines(s, l => l.Contains("OwnerSay str:2 42 hello x"));

        string asset = Encoding.Latin1.GetString(Host.Compile("default { state_entry() { llOwnerSay((string)osAddOne(1)); } }", true));
        Assert.Contains("ossl", asset);
        Assert.Contains("AddOne", asset);
        Assert.DoesNotContain("osAddOne", asset);
        Assert.Contains("OwnerSay", asset);
    }

    [Fact]
    public void Lsl_EveryTypeOfArgumentAndResultCanBeNamed()
    {
        Host.SetHostFunctions(string.Join("\n",
            "void osNote( string s, list l, vector v, rotation r, key k, float f, integer i )",
            "integer osInt(  )", "float osFloat(  )", "string osStr(  )", "key osKey(  )",
            "vector osVec(  )", "rotation osRot(  )", "list osList(  )"));
        Assert.NotEmpty(Host.Compile("""
            default
            {
                state_entry()
                {
                    integer i = osInt(); float f = osFloat(); string s = osStr(); key k = osKey();
                    vector v = osVec(); rotation r = osRot(); list l = osList();
                    osNote(s, l, v, r, k, f, i);
                }
            }
            """, true));
    }

    // ---- unknown names and wrong calls ----

    [Fact]
    public void Lsl_AMisspeltOsNameFailsToCompileWithItsLine()
    {
        Host.SetHostFunctions(AddOne);
        string text = CompileError("default\n{\n    state_entry() { llOwnerSay((string)osAdOne(1)); }\n}\n", true, out int code);

        Assert.Equal(-5, code);
        Assert.Contains("code -5: 3: ", text);
        Assert.Contains("osAdOne", text);
    }

    [Fact]
    public void Lsl_AWrongArgumentTypeFailsToCompileWithItsLine()
    {
        Host.SetHostFunctions(AddOne);
        string text = CompileError("default\n{\n    state_entry() { llOwnerSay((string)osAddOne(\"forty\")); }\n}\n", true, out int code);

        Assert.Equal(-5, code);
        Assert.Contains("code -5: 3: ", text);
    }

    [Fact]
    public void Lsl_AMisspeltLlNameStillFailsWithItsLine()
    {
        Host.SetHostFunctions(AddOne);
        string text = CompileError("default\n{\n    state_entry() { llOwnerSayy(\"x\"); }\n}\n", true, out int code);

        Assert.Equal(-5, code);
        Assert.Contains("code -5: 3: ", text);
    }

    // ---- what the list may name ----

    [Theory]
    [InlineData("integer llFoo( integer n )", "llFoo")]
    [InlineData("integer osfoo( integer n )", "osfoo")]
    [InlineData("integer fooBar( integer n )", "fooBar")]
    [InlineData("integer os( integer n )", "os")]
    [InlineData("integer osFoo( integer n", "osFoo")]
    [InlineData("quux osFoo( integer n )", "quux")]
    public void List_ALineThatNamesAnLlFunctionOrIsNotAnOsIwOrBotFunctionIsRefusedAndNothingChanges(string bad, string named)
    {
        Host.SetHostFunctions(AddOne);

        var e = Assert.Throws<SluaException>(() => Host.SetHostFunctions(AddOne + "\n" + bad));

        Assert.Equal(-3, e.Code);
        Assert.Contains(named, e.Message);
        Assert.NotEmpty(Host.Compile("default { state_entry() { llOwnerSay((string)osAddOne(1)); } }", true));   // the earlier list stands
    }

    [Fact]
    public void List_ANameThatLL_sOwnListHasCannotBeRedefined()
    {
        var e = Assert.Throws<SluaException>(() => Host.SetHostFunctions("integer llAbs( integer n )"));
        Assert.Equal(-3, e.Code);
    }

    [Fact]
    public void List_ANewListReplacesTheOldAndNullGivesBackWhatApi4Gave()
    {
        Host.SetHostFunctions(AddOne);
        Assert.NotEmpty(Host.Compile("default { state_entry() { llOwnerSay((string)osAddOne(1)); } }", true));

        Host.SetHostFunctions("integer iwTwice( integer n )");
        CompileError("default { state_entry() { llOwnerSay((string)osAddOne(1)); } }", true, out int code);
        Assert.Equal(-5, code);
        Assert.NotEmpty(Host.Compile("default { state_entry() { llOwnerSay((string)iwTwice(1)); } }", true));

        Host.SetHostFunctions(null);
        CompileError("default { state_entry() { llOwnerSay((string)iwTwice(1)); } }", true, out code);
        Assert.Equal(-10, code);
    }

    // ---- API 4 behaviour is unchanged for a host that gives no list ----

    [Fact]
    public void Api4_WithNoListAnOsCallIsRefusedWithTheTextApi4Gave()
    {
        Host.SetHostFunctions(null);
        Host.SetEngineIdentity("Example Engine", "Example Host", "//engine:example");

        string text = CompileError("default { state_entry() { llOwnerSay(osGetGridName()); } }", true, out int code);

        Assert.Equal(-10, code);
        Assert.EndsWith("osGetGridName: not available in the Example Engine script engine. LSL scripts that call os*, iw* or bot* functions run on the Example Host engine (script header //engine:example).", text);
    }

    [Fact]
    public void Api4_ScriptsThatCallNoHostFunctionCompileToTheSameBytesWithAndWithoutAList()
    {
        const string lsl = "default { state_entry() { llOwnerSay(\"hi \" + (string)llGetUnixTime()); } touch_start(integer n) { llSay(0, \"t\"); } }";
        const string slua = "LLEvents:on('touch_start', function() ossl.Nop() end) ll.OwnerSay('x')";
        Host.SetHostFunctions(null);
        byte[] lslBefore = Host.Compile(lsl, true), sluaBefore = Host.Compile(slua, false);

        Host.SetHostFunctions(AddOne + "\ninteger iwTwice( integer n )\nstring botHello( string who )");

        Assert.Equal(lslBefore, Host.Compile(lsl, true));
        Assert.Equal(sluaBefore, Host.Compile(slua, false));
    }

    // ---- SLua: the same functions by their namespaces ----

    [Fact]
    public void Slua_ASCriptCallsOssl_Iw_AndBotFunctionsTheHostRegistered()
    {
        using var rig = RigWithHostFunctions();
        using var s = rig.Run("ll.OwnerSay(tostring(ossl.AddOne(1)) .. ' ' .. tostring(iw.Twice(21)) .. ' ' .. bot.Hello('x'))");

        rig.StepUntilLines(s, l => l.Contains("OwnerSay str:2 42 hello x"));
    }

    [Fact]
    public void Slua_AndLslCallTheSameHostFunction()
    {
        Host.SetHostFunctions(AddOne);
        using var rig = RigWithHostFunctions();
        using var slua = rig.Run("ll.OwnerSay('slua ' .. tostring(ossl.AddOne(9)))", tag: 1);
        using var lsl = rig.Run("default { state_entry() { llOwnerSay(\"lsl \" + (string)osAddOne(9)); } }", lsl: true, tag: 2);

        rig.StepUntilLines(slua, l => l.Contains("OwnerSay str:slua 10"));
        rig.StepUntilLines(lsl, l => l.Contains("OwnerSay str:lsl 10"));
    }
}
