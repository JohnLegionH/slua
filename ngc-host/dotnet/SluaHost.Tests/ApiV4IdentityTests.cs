// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

/// <summary>
/// API version 4: the version check, the engine identity the host supplies for the library's refusal
/// text, and the line numbers of LSL compile errors.
/// </summary>
[Collection("engine identity")]
public class ApiV4IdentityTests
{
    // ---- The version check ----

    [Fact]
    public void ApiVersion_TheLibraryAndTheHostAreVersionFive()
    {
        Assert.Equal(5, Host.ApiVersion());
        Assert.Equal(5, Host.RequiredApiVersion);
        Assert.Contains("api=5", Host.Version());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void ApiVersion_AMismatchNamesBothNumbers(int library)
    {
        var e = Assert.Throws<ApiVersionMismatchException>(() => Host.VerifyApiVersion(library));
        Assert.Contains($"API version {library}", e.Message);
        Assert.Contains("API version 5", e.Message);
        Assert.Equal(library, e.LibraryVersion);
        Assert.Equal(5, e.RequiredVersion);
    }

    [Fact]
    public void ApiVersion_TheRequiredVersionPasses()
    {
        Host.VerifyApiVersion(Host.RequiredApiVersion);
    }

    [Fact]
    public void ApiVersion_InitRefusesALibraryOfAnotherVersion()
    {
        var e = Assert.Throws<ApiVersionMismatchException>(() => Host.InitWith(() => 3));
        Assert.Contains("API version 3", e.Message);
        Assert.Contains("API version 5", e.Message);
        Host.InitWith(() => Host.RequiredApiVersion);
    }

    // ---- The engine identity ----

    static void WithIdentity(string? engine, string? alt, string? header, Action body)
    {
        Host.SetEngineIdentity(engine, alt, header);
        try { body(); }
        finally { Host.SetEngineIdentity(null); }
    }

    static string RefusalText()
    {
        var e = Assert.Throws<SluaException>(() => Host.Compile("default { state_entry() { llOwnerSay(osGetGridName()); } }", true));
        Assert.Equal(-10, e.Code);
        return e.Message;
    }

    [Fact]
    public void Identity_TheRefusalNamesTheEnginesTheHostSupplied()
    {
        WithIdentity("Example Engine", "Example Host", "//engine:example", () =>
        {
            string text = RefusalText();
            Assert.Contains("osGetGridName", text);
            Assert.Contains("Example Engine", text);
            Assert.Contains("Example Host", text);
            Assert.Contains("//engine:example", text);
            Assert.Contains("os*, iw* or bot*", text);
        });
    }

    [Fact]
    public void Identity_NoNameIsBuiltIntoTheLibrary()
    {
        WithIdentity(null, null, null, () =>
        {
            string text = RefusalText();
            Assert.Contains("osGetGridName", text);
            Assert.Contains("os*, iw* or bot*", text);
            Assert.DoesNotContain("Phlox", text);
            Assert.DoesNotContain("PLACEHOLDER", text);
            Assert.DoesNotContain("//engine", text);
            Assert.Equal("", Host.EngineName);
        });
    }

    [Fact]
    public void Identity_TheOtherEngineWithoutAHeaderIsNamedWithoutOne()
    {
        WithIdentity("Example Engine", "Example Host", null, () =>
        {
            string text = RefusalText();
            Assert.Contains("Example Host", text);
            Assert.DoesNotContain("header", text);
        });
    }

    [Fact]
    public void Identity_TheEngineNameReadsBackAsSupplied()
    {
        WithIdentity("Exämple Engine ✓", "Example Host", "//engine:example", () =>
        {
            Assert.Equal("Exämple Engine ✓", Host.EngineName);
        });
        Assert.Equal("", Host.EngineName);
    }

    [Fact]
    public void Identity_ANameCanBeReplacedAndCleared()
    {
        WithIdentity("First Engine", "First Host", "//engine:first", () =>
        {
            Assert.Contains("First Host", RefusalText());
            Host.SetEngineIdentity("Second Engine", "Second Host", "//engine:second");
            string text = RefusalText();
            Assert.Contains("Second Host", text);
            Assert.DoesNotContain("First", text);
            Host.SetEngineIdentity("", "", "");
            Assert.DoesNotContain("Second", RefusalText());
            Assert.Equal("", Host.EngineName);
        });
    }

    // ---- LSL compile errors report the line they are on ----

    static string CompileError(string source, bool lsl)
    {
        var e = Assert.Throws<SluaException>(() => Host.Compile(source, lsl));
        Assert.Equal(-5, e.Code);
        return e.Message;
    }

    [Fact]
    public void LslLine_AnErrorOnTheFirstLineIsReportedOnLineOne()
    {
        Assert.Contains("code -5: 1: ", CompileError("default { state_entry() { llNoSuchFunction(); } }", true));
    }

    [Fact]
    public void LslLine_AnErrorFurtherDownIsReportedOnItsOwnLine()
    {
        const string source = "default\n{\n    state_entry()\n    {\n        llNoSuchFunction();\n    }\n}\n";
        Assert.Contains("code -5: 5: ", CompileError(source, true));
    }

    [Fact]
    public void LslLine_ATypeErrorIsReportedOnItsOwnLine()
    {
        const string source = "default\n{\n    state_entry()\n    {\n        llOwnerSay(\"fine\");\n        integer i = \"text\";\n    }\n}\n";
        Assert.Contains("code -5: 6: ", CompileError(source, true));
    }

    [Fact]
    public void LslLine_ALineBeforeTheErrorMovesItDownByOne()
    {
        const string source = "// a comment line\ndefault { state_entry() { llNoSuchFunction(); } }\n";
        Assert.Contains("code -5: 2: ", CompileError(source, true));
    }

    [Fact]
    public void LslLine_SLuaErrorsKeepTheirLine()
    {
        const string source = "local x = 1\nlocal y = 2\nlocal z = = 3\n";
        Assert.Contains("code -5: 3: ", CompileError(source, false));
    }
}
