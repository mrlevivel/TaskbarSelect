using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskbarSelect
{
    // Reads data from the already authenticated manual native image. No PE is
    // loaded, mapped as executable code, or admitted from a caller path.
    internal static class ScheduledBootstrapImage
    {
        internal const int BrokerResource = 210, MarkerResource = 211, Language = 1033;
        internal const string Marker = "TaskbarSelect.ScheduledOnly.v1";
        internal const int MaximumImage = 16 * 1024 * 1024;
        static IOException Invalid() { return new IOException("The approved scheduled administrator image is invalid."); }
        static void Require(bool condition) { if (!condition) throw Invalid(); }
        internal static byte[] Extract(byte[] manual, string approvedManualHash)
        {
            Require(manual != null && manual.Length <= MaximumImage && HashText(approvedManualHash) && Digest(manual) == approvedManualHash);
            var source = new Image(manual);
            Require(source.HasType(24) && !source.Has(10, MarkerResource));
            byte[] broker = source.Unique(10, BrokerResource, Language);
            var scheduled = new Image(broker);
            Require(!scheduled.HasType(24) && !scheduled.Has(10, BrokerResource));
            byte[] marker = scheduled.Unique(10, MarkerResource, Language);
            byte[] expected = Encoding.ASCII.GetBytes(Marker);
            Require(marker.Length == expected.Length);
            for (int i = 0; i < expected.Length; i++) Require(marker[i] == expected[i]);
            return broker;
        }
        internal static string Digest(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static bool HashText(string value)
        { if (value == null || value.Length != 64) return false; foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
        sealed class Section
        { internal uint Rva, VirtualSize, Offset, Size; }
        sealed class Resource
        { internal string Type, Id, Lang; internal int Offset, Length; }
        sealed class Image
        {
            readonly byte[] bytes;
            readonly List<Section> sections = new List<Section>();
            readonly List<Resource> resources = new List<Resource>();
            readonly HashSet<uint> directories = new HashSet<uint>();
            readonly HashSet<uint> entries = new HashSet<uint>();
            readonly HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int resourceBase, resourceLength, metadataEnd, nodes;
            uint resourceRva;
            internal Image(byte[] value)
            {
                Require(value != null && value.Length >= 256 && value.Length <= MaximumImage); bytes = value;
                Require(U16(0) == 0x5A4D);
                uint peRaw = U32(0x3C); Require(peRaw >= 64 && peRaw <= Int32.MaxValue);
                int pe = (int)peRaw; Range(pe, 24); Require(U32(pe) == 0x4550 && U16(pe + 4) == 0x8664);
                int count = U16(pe + 6), optionalSize = U16(pe + 20), optional = pe + 24;
                ushort characteristics = U16(pe + 22);
                Require(count >= 1 && count <= 96 && (characteristics & 2) != 0 && (characteristics & 0x2000) == 0 && optionalSize >= 240 && optionalSize <= 512);
                Range(optional, optionalSize); Require(U16(optional) == 0x20B && U16(optional + 68) == 2);
                uint dataCount = U32(optional + 108); Require(dataCount >= 15 && dataCount <= 16 && optionalSize >= 112 + dataCount * 8);
                Require(U32(optional + 112 + 13 * 8) == 0 && U32(optional + 116 + 13 * 8) == 0 &&
                    U32(optional + 112 + 14 * 8) == 0 && U32(optional + 116 + 14 * 8) == 0);
                int table = optional + optionalSize; Range(table, count * 40); int headerEnd = table + count * 40;
                for (int i = 0; i < count; i++) {
                    int at = table + i * 40;
                    var section = new Section { VirtualSize = U32(at + 8), Rva = U32(at + 12), Size = U32(at + 16), Offset = U32(at + 20) };
                    Require((ulong)section.Rva + Math.Max(section.VirtualSize, section.Size) <= UInt32.MaxValue);
                    if (section.Size != 0) { Require(section.Offset >= headerEnd && section.Offset <= Int32.MaxValue && section.Size <= Int32.MaxValue); Range((int)section.Offset, (int)section.Size); }
                    foreach (Section old in sections) {
                        Require(!Overlap(section.Rva, Math.Max(section.VirtualSize, section.Size), old.Rva, Math.Max(old.VirtualSize, old.Size)));
                        Require(!Overlap(section.Offset, section.Size, old.Offset, old.Size));
                    }
                    sections.Add(section);
                }
                uint imports = U32(optional + 120), importSize = U32(optional + 124);
                Imports(imports, importSize);
                resourceRva = U32(optional + 128); uint length = U32(optional + 132);
                Require(resourceRva != 0 && length >= 16 && length <= MaximumImage);
                resourceBase = Map(resourceRva, length); resourceLength = (int)length;
                Walk(0, 0, null, null);
                foreach (Resource item in resources) Require(item.Offset >= resourceBase + metadataEnd);
            }
            static bool Overlap(uint a, uint sizeA, uint b, uint sizeB)
            { return sizeA != 0 && sizeB != 0 && (ulong)a < (ulong)b + sizeB && (ulong)b < (ulong)a + sizeA; }
            int Map(uint rva, uint size)
            {
                Require(size > 0 && size <= Int32.MaxValue && (ulong)rva + size <= UInt32.MaxValue);
                int found = -1;
                foreach (Section section in sections) {
                    if (rva < section.Rva) continue;
                    ulong delta = (ulong)rva - section.Rva;
                    if (delta + size > section.Size) continue;
                    ulong offset = section.Offset + delta; Require(offset <= Int32.MaxValue && found < 0);
                    found = (int)offset; Range(found, (int)size);
                }
                Require(found >= 0); return found;
            }
            void Imports(uint rva, uint size)
            {
                Require(rva != 0 && size >= 40 && size <= 65536); int start = Map(rva, size), count = 0; bool ended = false;
                for (uint offset = 0; offset + 20 <= size; offset += 20) {
                    int at = start + (int)offset;
                    bool empty = U32(at) == 0 && U32(at + 4) == 0 && U32(at + 8) == 0 && U32(at + 12) == 0 && U32(at + 16) == 0;
                    if (empty) { ended = true; break; }
                    Require(++count == 1 && U32(at + 12) != 0 && U32(at + 16) != 0);
                    Require(String.Equals(Ascii(U32(at + 12)), "kernel32.dll", StringComparison.OrdinalIgnoreCase));
                }
                Require(ended && count == 1);
            }
            string Ascii(uint rva)
            {
                var name = new StringBuilder();
                for (uint i = 0; i < 64; i++) {
                    Require((ulong)rva + i <= UInt32.MaxValue); byte c = bytes[Map(rva + i, 1)];
                    if (c == 0) { Require(name.Length != 0); return name.ToString(); }
                    Require(c >= 32 && c <= 126); name.Append((char)c);
                }
                throw Invalid();
            }
            void ResourceRange(uint relative, uint size)
            { Require(relative <= Int32.MaxValue && size <= Int32.MaxValue && (ulong)relative + size <= (uint)resourceLength); }
            void Metadata(uint relative, uint size)
            { ResourceRange(relative, size); metadataEnd = Math.Max(metadataEnd, checked((int)(relative + size))); }
            void Walk(uint relative, int depth, string type, string id)
            {
                Require(depth <= 2 && ++nodes <= 1024 && directories.Add(relative)); Metadata(relative, 16);
                int at = resourceBase + (int)relative, named = U16(at + 12), numeric = U16(at + 14), count = named + numeric;
                Require(count > 0 && count <= 256); Metadata(relative, (uint)(16 + count * 8));
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++) {
                    uint rawName = U32(at + 16 + i * 8), child = U32(at + 20 + i * 8);
                    Require(((rawName & 0x80000000u) != 0) == (i < named)); string name = Name(rawName, depth == 2);
                    Require(names.Add(name)); bool directory = (child & 0x80000000u) != 0; uint location = child & 0x7FFFFFFFu;
                    if (depth < 2) { Require(directory); Walk(location, depth + 1, depth == 0 ? name : type, depth == 1 ? name : id); }
                    else {
                        Require(!directory && (rawName & 0x80000000u) == 0 && entries.Add(location)); Metadata(location, 16);
                        int data = resourceBase + (int)location; uint dataRva = U32(data), size = U32(data + 4);
                        Require(U32(data + 12) == 0 && size > 0 && size <= MaximumImage && dataRva >= resourceRva &&
                            (ulong)dataRva + size <= (ulong)resourceRva + (uint)resourceLength && identities.Add(type + "/" + id + "/" + name));
                        resources.Add(new Resource { Type = type, Id = id, Lang = name, Offset = Map(dataRva, size), Length = (int)size });
                    }
                }
            }
            string Name(uint value, bool language)
            {
                if ((value & 0x80000000u) == 0) { Require((value > 0 || language) && value <= 65535); return "i:" + value; }
                uint relative = value & 0x7FFFFFFFu; ResourceRange(relative, 2);
                ushort length = U16(resourceBase + (int)relative); Require(length > 0 && length <= 128); Metadata(relative, (uint)(2 + length * 2));
                string text = Encoding.Unicode.GetString(bytes, resourceBase + (int)relative + 2, length * 2); Require(text.IndexOf('\0') < 0); return "n:" + text;
            }
            internal bool HasType(int type) { foreach (Resource item in resources) if (item.Type == "i:" + type) return true; return false; }
            internal bool Has(int type, int id) { foreach (Resource item in resources) if (item.Type == "i:" + type && item.Id == "i:" + id) return true; return false; }
            internal byte[] Unique(int type, int id, int language)
            {
                Resource selected = null;
                foreach (Resource item in resources) if (item.Type == "i:" + type && item.Id == "i:" + id) { Require(selected == null && item.Lang == "i:" + language); selected = item; }
                Require(selected != null); byte[] value = new byte[selected.Length]; Array.Copy(bytes, selected.Offset, value, 0, value.Length); return value;
            }
            void Range(int offset, int length) { Require(offset >= 0 && length >= 0 && (long)offset + length <= bytes.Length); }
            ushort U16(int offset) { Range(offset, 2); return (ushort)(bytes[offset] | bytes[offset + 1] << 8); }
            uint U32(int offset) { Range(offset, 4); return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        }
    }
}
