using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TaskbarSelect
{
    // Synthetic PE bytes and hashes only. No image is loaded/executed and no
    // filesystem, Windows token, task, registry, UI or security state is used.
    internal static class ScheduledBootstrapImageTests
    {
        sealed class Item { internal int Type, Id, Language; internal byte[] Bytes; }
        sealed class Node { internal int Offset; internal readonly List<Edge> Edges = new List<Edge>(); }
        sealed class Edge { internal int Id, Entry, Data; internal Node Child; internal byte[] Bytes; }
        const int Pe = 0x80, Optional = 0x98, Sections = 0x188, ResourceFile = 0x400, ResourceRva = 0x2000;
        static Item Resource(int type, int id, int language, byte[] value)
        { return new Item { Type = type, Id = id, Language = language, Bytes = value }; }
        static byte[] Marker() { return Encoding.ASCII.GetBytes(ScheduledBootstrapImage.Marker); }
        static Item Mark(int id = 211, int language = 1033, byte[] value = null)
        { return Resource(10, id, language, value ?? Marker()); }
        static Item Manifest() { return Resource(24, 1, 0, Encoding.ASCII.GetBytes("owned inert manifest fixture")); }
        static byte[] Broker(params Item[] extra)
        { var items = new List<Item> { Mark() }; items.AddRange(extra); return Image(items.ToArray()); }
        static byte[] Manual(byte[] broker, params Item[] extra)
        { var items = new List<Item> { Resource(10, 210, 1033, broker), Manifest() }; items.AddRange(extra); return Image(items.ToArray()); }
        static int Align(int value, int size) { return (value + size - 1) / size * size; }
        static void W16(byte[] bytes, int at, int value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
        static void W32(byte[] bytes, int at, uint value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (8 * i)); }
        static uint R32(byte[] bytes, int at) { return BitConverter.ToUInt32(bytes, at); }
        static byte[] Image(Item[] items)
        {
            var root = new Node(); var types = new SortedDictionary<int, Node>();
            foreach (Item item in items) {
                Node type; if (!types.TryGetValue(item.Type, out type)) { type = new Node(); types.Add(item.Type, type); }
                Edge id = type.Edges.Find(e => e.Id == item.Id);
                if (id == null) { id = new Edge { Id = item.Id, Child = new Node() }; type.Edges.Add(id); }
                id.Child.Edges.Add(new Edge { Id = item.Language, Bytes = item.Bytes });
            }
            foreach (var type in types) { type.Value.Edges.Sort((a, b) => a.Id.CompareTo(b.Id)); foreach (Edge id in type.Value.Edges) id.Child.Edges.Sort((a, b) => a.Id.CompareTo(b.Id)); root.Edges.Add(new Edge { Id = type.Key, Child = type.Value }); }
            int cursor = 0; var leaves = new List<Edge>(); Assign(root, ref cursor, leaves);
            foreach (Edge leaf in leaves) { leaf.Entry = cursor; cursor += 16; }
            cursor = Align(cursor, 4);
            foreach (Edge leaf in leaves) { leaf.Data = cursor; cursor += Align(leaf.Bytes.Length, 4); }
            int resourceSize = cursor, rawSize = Align(resourceSize, 0x200); byte[] image = new byte[ResourceFile + rawSize];
            W16(image, 0, 0x5A4D); W32(image, 0x3C, Pe); W32(image, Pe, 0x4550); W16(image, Pe + 4, 0x8664); W16(image, Pe + 6, 2);
            W16(image, Pe + 20, 240); W16(image, Pe + 22, 0x22); W16(image, Optional, 0x20B); W32(image, Optional + 16, 0x1000);
            W32(image, Optional + 32, 0x1000); W32(image, Optional + 36, 0x200); W32(image, Optional + 56, (uint)(0x2000 + Align(resourceSize, 0x1000)));
            W32(image, Optional + 60, 0x200); W16(image, Optional + 68, 2); W32(image, Optional + 108, 16);
            W32(image, Optional + 120, 0x1100); W32(image, Optional + 124, 40); W32(image, Optional + 128, ResourceRva); W32(image, Optional + 132, (uint)resourceSize);
            Encoding.ASCII.GetBytes(".text").CopyTo(image, Sections); W32(image, Sections + 8, 0x200); W32(image, Sections + 12, 0x1000); W32(image, Sections + 16, 0x200); W32(image, Sections + 20, 0x200); W32(image, Sections + 36, 0x60000020);
            Encoding.ASCII.GetBytes(".rsrc").CopyTo(image, Sections + 40); W32(image, Sections + 48, (uint)resourceSize); W32(image, Sections + 52, ResourceRva); W32(image, Sections + 56, (uint)rawSize); W32(image, Sections + 60, ResourceFile); W32(image, Sections + 76, 0x40000040);
            W32(image, 0x300, 0x1150); W32(image, 0x30C, 0x1140); W32(image, 0x310, 0x1160); Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(image, 0x340);
            Emit(root, image);
            foreach (Edge leaf in leaves) { W32(image, ResourceFile + leaf.Entry, (uint)(ResourceRva + leaf.Data)); W32(image, ResourceFile + leaf.Entry + 4, (uint)leaf.Bytes.Length); leaf.Bytes.CopyTo(image, ResourceFile + leaf.Data); }
            return image;
        }
        static void Assign(Node node, ref int cursor, List<Edge> leaves)
        { node.Offset = cursor; cursor += 16 + node.Edges.Count * 8; foreach (Edge edge in node.Edges) if (edge.Child != null) Assign(edge.Child, ref cursor, leaves); else leaves.Add(edge); }
        static void Emit(Node node, byte[] image)
        {
            W16(image, ResourceFile + node.Offset + 14, node.Edges.Count);
            for (int i = 0; i < node.Edges.Count; i++) { Edge edge = node.Edges[i]; W32(image, ResourceFile + node.Offset + 16 + i * 8, (uint)edge.Id); W32(image, ResourceFile + node.Offset + 20 + i * 8, edge.Child == null ? (uint)edge.Entry : 0x80000000u | (uint)edge.Child.Offset); if (edge.Child != null) Emit(edge.Child, image); }
        }
        static bool Reject(byte[] image, string hash = null)
        { try { ScheduledBootstrapImage.Extract(image, hash ?? (image == null ? new string('0', 64) : ScheduledBootstrapImage.Digest(image))); return false; } catch (IOException) { return true; } }
        internal static void Run(Action<bool, string> check)
        {
            byte[] broker = Broker(), manual = Manual(broker); string approval = ScheduledBootstrapImage.Digest(manual);
            byte[] result = ScheduledBootstrapImage.Extract(manual, approval);
            check(Equal(result, broker) && result != broker && ScheduledBootstrapImage.Digest(result) != approval,
                "Only the exact scheduled-only PE data is extracted from the hash-admitted manual parent; its ledger digest is separate");
            result[0] ^= 1;
            check(ScheduledBootstrapImage.Digest(manual) == approval && Equal(ScheduledBootstrapImage.Extract(manual, approval), broker),
                "Extracted bytes are owned copies and cannot mutate the approved parent resource");
            byte[] changed = (byte[])manual.Clone(); changed[changed.Length - 1] ^= 1;
            check(Reject(changed, approval) && Reject(manual, approval.ToUpperInvariant()) && Reject(manual, new string('0', 64)) && Reject(manual, "bad"),
                "Source tampering, noncanonical digest and unknown approval fail before resource use");
            check(Reject(null) && Reject(new byte[255]) && Reject(new byte[ScheduledBootstrapImage.MaximumImage + 1], approval),
                "Missing, truncated and overbound parent images never reach the reader");
            check(Reject(Image(new[] { Manifest() })) && Reject(Image(new[] { Resource(10, 210, 1033, broker) })) && Reject(Manual(broker, Mark())),
                "The manual source must retain its manifest, have one broker resource and never carry the scheduled-only role marker");
            foreach (byte[] invalid in new[] {
                Image(new[] { Mark(212) }), Image(new[] { Mark(211, 1036) }), Image(new[] { Mark(211, 0) }),
                Image(new[] { Mark(211, 1033, Encoding.ASCII.GetBytes(ScheduledBootstrapImage.Marker + "\0")) }),
                Image(new[] { Mark(211, 1033, Encoding.ASCII.GetBytes("TaskbarSelect.ScheduledOnly.v2")) }),
                Broker(Manifest()), Broker(Resource(10, 210, 1033, broker)), Broker(Mark(211, 1036)), Broker(Mark()) })
                check(Reject(Manual(invalid)), "Missing/wrong/duplicate role data, another language, NUL suffix, nested broker or any RT_MANIFEST rejects scheduled code");
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                b => W16(b, 0, 0), b => W32(b, 0x3C, UInt32.MaxValue), b => W32(b, Pe, 0), b => W16(b, Pe + 4, 0x14C),
                b => W16(b, Pe + 6, 97), b => W16(b, Pe + 20, 239), b => W16(b, Pe + 22, 0x2002), b => W16(b, Optional, 0x10B),
                b => W16(b, Optional + 68, 3), b => W32(b, Optional + 108, 17), b => W32(b, Optional + 112 + 13 * 8, 0x1100),
                b => W32(b, Optional + 116 + 14 * 8, 72), b => W32(b, Optional + 128, 0xFFFFFFF0), b => W32(b, Optional + 132, UInt32.MaxValue),
                b => W32(b, Sections + 60, 0xFFFFFFF0), b => W32(b, Sections + 56, UInt32.MaxValue), b => W32(b, Sections + 52, 0xFFFFFFF0),
                b => W32(b, Sections + 52, 0x1000), b => W32(b, Sections + 60, 0x200), b => W32(b, Optional + 120, 0x5000),
                b => W32(b, Optional + 124, 20), b => W32(b, 0x30C, 0x5000), b => { Array.Clear(b, 0x340, 16); Encoding.ASCII.GetBytes("USER32.dll\0").CopyTo(b, 0x340); },
                b => { for (int i = 0; i < 64; i++) b[0x340 + i] = (byte)'A'; },
                b => { W32(b, Optional + 124, 60); W32(b, 0x320, 0x1140); W32(b, 0x324, 0x1160); } }) {
                byte[] malformed = (byte[])manual.Clone(); mutate(malformed);
                check(Reject(malformed), "Malformed/truncated/wrong-architecture/DLL/CLR/delayed or non-Kernel32 parent layout fails closed");
                byte[] inner = (byte[])broker.Clone(); mutate(inner);
                check(Reject(Manual(inner)), "Every same PE/import bound is independently required for the extracted scheduled image");
            }
            ResourceBounds(check, manual);
        }
        static void ResourceBounds(Action<bool, string> check, byte[] source)
        {
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                b => W16(b, ResourceFile + 14, 257), b => W16(b, ResourceFile + 12, 1),
                b => W16(b, ResourceFile + 14, 0), b => W32(b, ResourceFile + 20, 0x80000000),
                b => W32(b, ResourceFile + 20, 0xFFFFFFFF), b => W32(b, ResourceFile + 16, 0x80000000u | 0x7FFFFFF0u),
                b => W32(b, ResourceFile + 24, 10), b => W32(b, ResourceFile + 20, 0),
                b => { int type = ResourceFile + (int)(R32(b, ResourceFile + 20) & 0x7FFFFFFF); W32(b, type + 20, 0x7FFFFFF0); },
                b => { int type = ResourceFile + (int)(R32(b, ResourceFile + 20) & 0x7FFFFFFF); int lang = ResourceFile + (int)(R32(b, type + 20) & 0x7FFFFFFF); W32(b, lang + 20, 0x80000000); },
                b => { int leaf = FirstLeaf(b); W32(b, leaf, 0xFFFFFFF0); W32(b, leaf + 4, 0x100); },
                b => W32(b, FirstLeaf(b) + 4, UInt32.MaxValue), b => W32(b, FirstLeaf(b) + 12, 1),
                b => { int leaf = FirstLeaf(b); W32(b, leaf, ResourceRva); W32(b, leaf + 4, 16); } }) {
                byte[] malformed = (byte[])source.Clone(); mutate(malformed);
                check(Reject(malformed), "Resource cycles/duplicates/wrong-depth/offset overflow/truncation/metadata overlap never produce an executable copy");
            }
            byte[] wrongLanguage = Image(new[] { Resource(10, 210, 0, Broker()), Manifest() });
            check(Reject(wrongLanguage), "The approved broker is bound to exact numeric resource210/language1033, never a neutral fallback");
        }
        static int FirstLeaf(byte[] image)
        { int type = ResourceFile + (int)(R32(image, ResourceFile + 20) & 0x7FFFFFFF), lang = ResourceFile + (int)(R32(image, type + 20) & 0x7FFFFFFF); return ResourceFile + (int)R32(image, lang + 20); }
        static bool Equal(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
}
