// Copyright (c) 2026 Legion Builds
// Licensed under the MIT License; see LICENSE.txt in the repository root.
using System.Runtime.InteropServices;
using System.Text;

namespace SluaHost;

/// <summary>
/// Native memory for the strings and arrays of one boundary crossing. Everything it hands out
/// stays valid until <see cref="Dispose"/> or <see cref="Reset"/>.
/// </summary>
internal sealed unsafe class Arena : IDisposable
{
    readonly List<IntPtr> _blocks = new();

    public void* Alloc(nuint bytes)
    {
        void* p = NativeMemory.AllocZeroed(bytes == 0 ? 1 : bytes);
        _blocks.Add((IntPtr)p);
        return p;
    }

    /// <summary>Copies the text as zero-terminated UTF-8 and returns its byte length without the terminator.</summary>
    public byte* Utf8(string s, out int len)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        len = b.Length;
        byte* p = (byte*)Alloc((nuint)b.Length + 1);
        b.CopyTo(new Span<byte>(p, b.Length));
        return p;
    }

    public void Reset()
    {
        foreach (IntPtr p in _blocks) NativeMemory.Free((void*)p);
        _blocks.Clear();
    }

    public void Dispose() => Reset();
}
