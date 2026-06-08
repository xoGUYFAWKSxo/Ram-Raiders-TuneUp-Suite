using System;
using System.IO;
using TuneUpEngine;
using Xunit;

namespace TuneUpEngine.Tests
{
    // Locks in the "byte-for-byte duplicate" fix. The headline guarantee is that two files which share
    // a size AND an identical first 1 MB but differ later are NOT treated as duplicates — the exact
    // false-positive the old first-1MB-only hash produced.
    public class AdvancedScannerTests : IDisposable
    {
        private const int OneMb = 1024 * 1024;
        private readonly string _dir;
        private readonly AdvancedScanner _scanner = new AdvancedScanner();

        public AdvancedScannerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "RRTU_Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        private string CreateFile(string name, byte head, byte tail, int headLen, int tailLen)
        {
            string path = Path.Combine(_dir, name);
            byte[] bytes = new byte[headLen + tailLen];
            Array.Fill(bytes, head, 0, headLen);
            Array.Fill(bytes, tail, headLen, tailLen);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [Fact]
        public void SameFirstMegabyteButDifferentTail_IsNotADuplicate()
        {
            // Identical first 1 MB, identical total size, but different trailing 0.5 MB.
            string a = CreateFile("a.bin", head: 0xAB, tail: 0x01, headLen: OneMb, tailLen: OneMb / 2);
            string b = CreateFile("b.bin", head: 0xAB, tail: 0x02, headLen: OneMb, tailLen: OneMb / 2);

            // The cheap pre-filter (first 1 MB) collides — which is why the old code wrongly matched them.
            Assert.Equal(_scanner.GetPartialHash(a), _scanner.GetPartialHash(b));

            // The full-file confirmation must distinguish them, so they are reported as distinct.
            Assert.NotEqual(_scanner.GetFullHash(a), _scanner.GetFullHash(b));
        }

        [Fact]
        public void IdenticalContent_IsADuplicate()
        {
            string a = CreateFile("c1.bin", head: 0xCD, tail: 0x09, headLen: OneMb, tailLen: OneMb / 4);
            string b = CreateFile("c2.bin", head: 0xCD, tail: 0x09, headLen: OneMb, tailLen: OneMb / 4);

            Assert.Equal(_scanner.GetFullHash(a), _scanner.GetFullHash(b));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }
}
