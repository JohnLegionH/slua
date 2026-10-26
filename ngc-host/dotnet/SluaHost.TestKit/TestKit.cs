// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Collections.Concurrent;

namespace SluaHost;

/// <summary>
/// The host functions the deciding tests and the cross-process tool register,
/// plus the sink they report into. Kept in a test-only project, shared by the
/// tests and the tool so a second process binds exactly the same function list.
/// </summary>
public sealed class TestKit
{
    public readonly ConcurrentQueue<string> Lines = new();
    /// <summary>Async requests started by scripts, by script tag.</summary>
    public readonly ConcurrentDictionary<long, long> Pending = new();
    /// <summary>Results the host has completed, by script tag.</summary>
    public readonly ConcurrentDictionary<long, Value> Results = new();
    public long NopCalls;

    public static string Join(ReadOnlySpan<Value> a)
    {
        var parts = new string[a.Length];
        for (int i = 0; i < a.Length; i++) parts[i] = a[i].ToString();
        return string.Join(" ", parts);
    }

    public string[] Snapshot() => Lines.ToArray();

    static long AsLong(Value v) => v.Type == ValueType.Number ? (long)v.N : v.I;

    /// <summary>Registers the standard function set. Pass extra=true to add one more function, which changes the bound list.</summary>
    public void Register(Vm vm, bool extra = false)
    {
        vm.Register("ossl", "GetGridName", (_, _) => new[] { Value.Str("Example Grid") });
        vm.Register("ossl", "Report", (tag, a) => { Lines.Enqueue(Join(a)); return null; });
        vm.Register("ossl", "Add", (_, a) => new[] { Value.Int(AsLong(a[0]) + AsLong(a[1])) });
        vm.Register("ossl", "Echo", (_, a) => a.ToArray());
        vm.Register("ossl", "Boom", (_, _) => throw new InvalidOperationException("boom from host"));
        vm.Register("ossl", "Nop", (_, _) => { Interlocked.Increment(ref NopCalls); return null; });
        vm.Register("ossl", "StartAsync", (tag, a) => { Pending[tag] = AsLong(a[0]); return null; });
        vm.Register("ossl", "StartAsyncYield", (tag, a) => { Pending[tag] = AsLong(a[0]); return Host.Yield; });
        vm.Register("ossl", "TakeResult", (tag, _) => Results.TryRemove(tag, out var v) ? new[] { v } : new[] { Value.Nil });
        vm.Register("ll", "OwnerSay", (tag, a) => { Lines.Enqueue("OwnerSay " + Join(a)); return null; });
        vm.Register("ll", "Say", (tag, a) => { Lines.Enqueue("Say " + Join(a)); return null; });
        vm.Register("ll", "GetUnixTime", (_, _) => new[] { Value.Int(1700000000) });
        if (extra) vm.Register("ossl", "Extra", (_, _) => null);
    }
}
