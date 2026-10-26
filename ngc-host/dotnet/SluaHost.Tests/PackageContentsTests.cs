// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using SluaHost;
using Xunit;

namespace SluaHost.Tests;

public class PackageContentsTests
{
    /// <summary>
    /// The host functions the tests register live in a test-only project. The library assembly,
    /// which is what the NuGet package carries, has no such type.
    /// </summary>
    [Fact]
    public void B1_TheLibraryAssemblyHasNoTestKit()
    {
        Assert.Null(typeof(Vm).Assembly.GetType("SluaHost.TestKit"));
        Assert.NotSame(typeof(Vm).Assembly, typeof(TestKit).Assembly);
    }
}
