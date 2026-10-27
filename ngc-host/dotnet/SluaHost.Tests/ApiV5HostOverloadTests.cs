// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// API 5: a host function name may be listed several times with different parameter types. An LSL call picks among the
/// signatures of its name the way the Phlox script engine's compiler does: only signatures with the call's number of arguments
/// are candidates; an argument scores 2 when its type is the parameter's type and 1 when it reaches it by LSL's implicit
/// conversion (integer to float, key to string, string to key), and a signature any argument cannot reach is out; the best total
/// wins; two signatures with the best total are a compile error that names the function and both signatures. An argument of
/// unknown type (one that already has an error) matches anything and scores nothing. The call runs the one host function
/// registered under the name, with its arguments converted for the signature that was picked. An exact repeat of a parameter
/// list is still refused. The list is process-wide, so these tests share the collection of the other process-wide settings.
/// </summary>
[Collection("engine identity")]
public sealed class ApiV5HostOverloadTests : IDisposable
{
    public void Dispose()
    {
        Host.SetHostFunctions(null);
        Host.SetEngineIdentity(null);
    }

    // A refused list fails the test on an assertion, with the library's message.
    static void SetList(params string[] lines)
    {
        try { Host.SetHostFunctions(string.Join("\n", lines)); }
        catch (SluaException e) { Assert.Fail("the list was refused: " + e.Message); }
    }

    static byte[] Compiled(string source, bool lsl)
    {
        try { return Host.Compile(source, lsl); }
        catch (SluaException e) { Assert.Fail("did not compile: " + e.Message); throw; }
    }

    static string CompileError(string source, out int code)
    {
        var e = Assert.Throws<SluaException>(() => Host.Compile(source, true));
        code = e.Code;
        return e.Message;
    }

    static string Body(string statements) => "default { state_entry() { " + statements + " } }";

    // A host function that records how it was called: its name, how many arguments, and the type of each.
    static void Record(V4Rig rig, string table, string name, Func<Value[], Value[]?>? result = null) =>
        rig.Vm.Register(table, name, (_, span) =>
        {
            Value[] a = span.ToArray();
            rig.Kit.Lines.Enqueue("Called " + name + " n=" + a.Length + " " + string.Join(",", a.Select(x => x.Type)));
            return result?.Invoke(a);
        });

    // Runs an LSL script and returns the calls the host function saw, in order.
    static string[] Calls(V4Rig rig, string statements, int expected)
    {
        var s = rig.Fresh(Compiled(Body(statements), true));
        s.Start();
        rig.StepUntilLines(s, l => l.Count(x => x.StartsWith("Called ")) >= expected);
        s.Dispose();
        return rig.Kit.Snapshot().Where(x => x.StartsWith("Called ")).ToArray();
    }

    // ---- picking by the number of arguments ----

    [Fact]
    public void Lsl_SignaturesOfDifferentLengthsAreToldApartByTheNumberOfArguments()
    {
        SetList("void osNpcSay( key npc, string message )", "void osNpcSay( key npc, integer channel, string message )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "NpcSay");

        string[] calls = Calls(rig, "osNpcSay((key)\"k\", \"hi\"); osNpcSay((key)\"k\", 3, \"hi\");", 2);

        Assert.Equal(2, calls.Length);
        Assert.StartsWith("Called NpcSay n=2 ", calls[0]);
        Assert.StartsWith("Called NpcSay n=3 ", calls[1]);
    }

    // ---- picking by type when the length is the same ----

    [Fact]
    public void Lsl_SignaturesOfTheSameLengthAreToldApartByTheTypeOfTheArguments()
    {
        SetList("void osNote( string name, string text )", "void osNote( string name, list text )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Note");

        string[] calls = Calls(rig, "osNote(\"n\", \"text\"); osNote(\"n\", [\"a\", \"b\"]);", 2);

        Assert.Equal(2, calls.Length);
        Assert.EndsWith("String,String", calls[0]);
        Assert.EndsWith("String,List", calls[1]);
    }

    [Fact]
    public void Lsl_FloatVectorAndRotationFormsOfOneNameEachGetTheirOwnCall()
    {
        SetList("integer osNear( float a, float b )", "integer osNear( vector a, vector b )", "integer osNear( rotation a, rotation b )",
                "integer osNear( float a, float b, float c )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Near", _ => new[] { Value.Int(1) });

        string[] calls = Calls(rig, "osNear(1.5, 2.5); osNear(<1,2,3>, <4,5,6>); osNear(<1,2,3,4>, <1,2,3,4>); osNear(1.0, 2.0, 0.5);", 4);

        Assert.Equal(4, calls.Length);
        Assert.EndsWith("n=2 Number,Number", calls[0]);
        Assert.EndsWith("n=2 Vector,Vector", calls[1]);
        Assert.EndsWith("n=2 Rotation,Rotation", calls[2]);
        Assert.EndsWith("n=3 Number,Number,Number", calls[3]);
    }

    // ---- exact against converted ----

    [Fact]
    public void Lsl_AnExactMatchBeatsOneThatNeedsAnIntegerMadeIntoAFloat_WhateverTheListedOrder()
    {
        SetList("void osPick( float f )", "void osPick( integer i )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Pick");

        string[] calls = Calls(rig, "osPick(3); osPick(3.5);", 2);

        Assert.Equal(2, calls.Length);
        Assert.EndsWith("n=1 Int", calls[0]);
        Assert.EndsWith("n=1 Number", calls[1]);
    }

    [Fact]
    public void Lsl_WithOnlyAFloatSignatureAnIntegerArgumentIsAcceptedAndPassedAsAFloat()
    {
        SetList("void osOnly( float f )", "void osOnly( float f, float g )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Only");

        string[] calls = Calls(rig, "osOnly(3);", 1);

        Assert.EndsWith("n=1 Number", Assert.Single(calls));
    }

    [Fact]
    public void Lsl_AStringReachesAKeyParameterAndASignatureItCannotReachIsOut()
    {
        SetList("void osAim( vector v, integer i )", "void osAim( key k, integer i )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Aim");

        string[] calls = Calls(rig, "osAim(\"abc\", 1);", 1);

        string line = Assert.Single(calls);
        Assert.DoesNotContain("Vector", line);
        Assert.EndsWith("Int", line);
    }

    // ---- ties ----

    [Fact]
    public void Lsl_TwoSignaturesWithTheBestScoreAreACompileErrorNamingTheFunctionAndBoth()
    {
        SetList("void osTie( string s, integer i )", "void osTie( key k, float f )");

        // (key, integer): the first needs the key made a string, the second the integer made a float; 3 against 3.
        string text = CompileError("default\n{\n    state_entry() { osTie((key)\"a\", 1); }\n}\n", out int code);

        Assert.Equal(-5, code);
        Assert.Contains("code -5: 3: ", text);
        Assert.Contains("osTie", text);
        Assert.Contains("osTie(string, integer)", text);
        Assert.Contains("osTie(key, float)", text);
    }

    [Fact]
    public void Lsl_ACallThatOneSignatureWinsIsNotATie()
    {
        SetList("void osTie( string s, integer i )", "void osTie( key k, float f )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Tie");

        string[] calls = Calls(rig, "osTie(\"a\", 1); osTie((key)\"a\", 1.5);", 2);

        Assert.Equal(2, calls.Length);
        Assert.EndsWith("Int", calls[0]);
        Assert.EndsWith("Number", calls[1]);
    }

    // ---- the result type belongs to the signature ----

    [Fact]
    public void Lsl_EachSignatureHasItsOwnResultType()
    {
        SetList("vector osMix( vector a, vector b, float t )", "rotation osMix( rotation a, rotation b, float t )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "Mix", a => new[] { a[0].Type == ValueType.Vector ? Value.Vec(9, 9, 9) : Value.Rot(1, 2, 3, 4) });

        string[] calls = Calls(rig, "vector v = osMix(<1,2,3>, <4,5,6>, 0.5); rotation r = osMix(<1,2,3,4>, <1,2,3,4>, 0.5);", 2);
        Assert.EndsWith("Vector,Vector,Number", calls[0]);
        Assert.EndsWith("Rotation,Rotation,Number", calls[1]);

        string text = CompileError(Body("rotation r = osMix(<1,2,3>, <4,5,6>, 0.5);"), out int code);
        Assert.Equal(-5, code);
        Assert.Contains("assigned a vector value", text);
        CompileError(Body("vector v = osMix(<1,2,3,4>, <1,2,3,4>, 0.5);"), out code);
        Assert.Equal(-5, code);
    }

    // ---- a call no signature takes ----

    [Fact]
    public void Lsl_ACallNoSignatureTakesIsACompileErrorThatNamesTheFunction()
    {
        SetList("void osNone( integer a )", "void osNone( integer a, integer b )");

        string wrongType = CompileError("default\n{\n    state_entry() { osNone(\"x\"); }\n}\n", out int code);
        Assert.Equal(-5, code);
        Assert.Contains("code -5: 3: ", wrongType);
        Assert.Contains("osNone", wrongType);

        string tooMany = CompileError(Body("osNone(1, 2, 3);"), out code);
        Assert.Equal(-5, code);
        Assert.Contains("osNone", tooMany);

        string tooFew = CompileError(Body("osNone();"), out code);
        Assert.Equal(-5, code);
        Assert.Contains("osNone", tooFew);
    }

    [Fact]
    public void Lsl_AnArgumentThatAlreadyHasAnErrorDoesNotAddAnAmbiguityError()
    {
        SetList("void osPick( float f )", "void osPick( integer i )");

        string text = CompileError(Body("osPick(notDeclared);"), out int code);

        Assert.Equal(-5, code);
        Assert.Contains("notDeclared", text);
        Assert.DoesNotContain("ambiguous", text);
    }

    // ---- the list ----

    [Fact]
    public void List_SeveralSignaturesOfOneNameAreAccepted_AndOneWithTheSameParameterTypesIsStillRefused()
    {
        SetList("integer osDup( integer n )", "integer osDup( integer n, integer m )");

        var e = Assert.Throws<SluaException>(() => Host.SetHostFunctions("integer osDup( integer n )\nstring osDup( integer other )"));

        Assert.Equal(-3, e.Code);
        Assert.Contains("osDup", e.Message);
        Assert.Contains("twice", e.Message);
        // the earlier list stands, both signatures
        Assert.NotEmpty(Compiled(Body("integer a = osDup(1); integer b = osDup(1, 2);"), true));
    }

    [Fact]
    public void List_ABadLineAfterOverloadsLeavesTheEarlierOverloadsInPlace()
    {
        SetList("integer osDup( integer n )", "integer osDup( integer n, integer m )");

        var e = Assert.Throws<SluaException>(() => Host.SetHostFunctions("integer osDup( integer n )\nvoid osOther( integer n )\ninteger llAbs( integer n )"));

        Assert.Equal(-3, e.Code);
        Assert.NotEmpty(Compiled(Body("integer a = osDup(1); integer b = osDup(1, 2);"), true));
        CompileError(Body("osOther(1);"), out int code);
        Assert.Equal(-5, code);
    }

    [Fact]
    public void List_ANewListDropsEverySignatureOfTheOldOne()
    {
        SetList("integer osMany( integer n )", "integer osMany( integer n, integer m )");
        Assert.NotEmpty(Compiled(Body("integer b = osMany(1, 2);"), true));

        SetList("integer osMany( integer n )");

        Assert.NotEmpty(Compiled(Body("integer a = osMany(1);"), true));
        CompileError(Body("integer b = osMany(1, 2);"), out int code);
        Assert.Equal(-5, code);

        SetList("integer iwOther( integer n )");
        CompileError(Body("integer a = osMany(1);"), out code);
        Assert.Equal(-5, code);
    }

    [Fact]
    public void List_ANameWithOneSignatureCompilesToTheSameBytesWhateverElseIsOverloaded()
    {
        const string script = "default { state_entry() { llOwnerSay((string)osAddOne(41)); } }";
        SetList("integer osAddOne( integer n )");
        byte[] alone = Compiled(script, true);

        SetList("integer osAddOne( integer n )", "void osNpcSay( key npc, string m )", "void osNpcSay( key npc, integer c, string m )");

        Assert.Equal(alone, Compiled(script, true));
    }

    // ---- SLua ----

    [Fact]
    public void Slua_ASluaScriptCallsTheOneHostFunctionWhateverItsLslSignatures()
    {
        SetList("void osNpcSay( key npc, string message )", "void osNpcSay( key npc, integer channel, string message )");
        using var rig = new V4Rig();
        Record(rig, "ossl", "NpcSay");
        using var s = rig.Fresh(Compiled("ossl.NpcSay('k', 'hi') ossl.NpcSay('k', 3, 'hi') ossl.NpcSay('only one')", false));
        s.Start();

        rig.StepUntilLines(s, l => l.Count(x => x.StartsWith("Called ")) >= 3);

        string[] calls = rig.Kit.Snapshot().Where(x => x.StartsWith("Called ")).ToArray();
        Assert.StartsWith("Called NpcSay n=2 ", calls[0]);
        Assert.StartsWith("Called NpcSay n=3 ", calls[1]);
        Assert.StartsWith("Called NpcSay n=1 ", calls[2]);
    }
}
