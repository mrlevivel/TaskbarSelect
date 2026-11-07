using System;
using System.Collections.Generic;
using System.Drawing;

namespace TaskbarSelect
{
    internal enum FocusIndicatorMode { Off = 0, MutedFrame = 1, InsetEdge = 2 }
    internal enum FocusFrameFinish { MutedEdge = 0, AccentOutline = 1 }
    internal enum FocusLightingMode { None = 0, SubtleGlow = 1, Illuminated = 2, EnergyFlow = 3 }

    internal static class FocusAppearance
    {
        sealed class Palette
        {
            internal readonly string Name;
            internal readonly Color LightAccent, DarkAccent, LightFrame, DarkFrame;
            internal readonly int Family, FamilyOrder;
            internal Palette(string name, int lightAccent, int darkAccent, int lightFrame, int darkFrame, int family, int familyOrder)
            {
                Name = name; LightAccent = Rgb(lightAccent); DarkAccent = Rgb(darkAccent);
                LightFrame = Rgb(lightFrame); DarkFrame = Rgb(darkFrame);
                Family = family; FamilyOrder = familyOrder;
            }
        }

        static readonly string[] families = { "Default", "Warm", "Greens", "Cool", "Neutrals" };
        static readonly Palette[] palettes = {
            // Original keeps the released app's accent exactly, rather than a mockup approximation.
            new Palette("Original", 0x007359, 0x5CDDB7, 0x83958F, 0x8BABA8, 2, 0),
            new Palette("Coral", 0xC36355, 0xF19080, 0xA9928C, 0xBD9A93, 1, 2),
            new Palette("Mint", 0x3B977A, 0xA1E5BC, 0x899B90, 0x9CB7A7, 2, 1),
            new Palette("Lavender", 0x8B6FB2, 0xC2A8E8, 0x988BAA, 0xAC9BBE, 3, 2),
            new Palette("Sky", 0x3A8DBA, 0x8BCDF0, 0x869AA8, 0x94AFBD, 3, 0),
            new Palette("Peach", 0xC47D50, 0xF0B58A, 0xAB9787, 0xBDA895, 1, 3),
            new Palette("Blaze Orange", 0xBD681F, 0xF58B38, 0xAB9582, 0xBB9C80, 1, 4),
            new Palette("Forest Green", 0x3F8656, 0x78BD86, 0x8D9C8D, 0x9CAF9C, 0, 0),
            new Palette("Rose", 0xA94C64, 0xD98A9D, 0xA58E95, 0xB39AA3, 1, 1),
            new Palette("Amber", 0xA06D23, 0xDBAC60, 0xA39780, 0xB8AA8B, 1, 5),
            new Palette("Olive", 0x738449, 0xA6B879, 0x939B83, 0xA6B28F, 2, 3),
            new Palette("Slate", 0x64768B, 0xA3B4C7, 0x8B95A2, 0x9EACBB, 4, 1),
            new Palette("Sand", 0x907B51, 0xCCB991, 0xA09582, 0xB5AA96, 4, 0),
            new Palette("Ruby", 0xB93950, 0xEE6677, 0xA8868F, 0xBA909B, 1, 0),
            new Palette("Cobalt", 0x245BCD, 0x6C9EFF, 0x8593AA, 0x92A6C2, 3, 1),
            new Palette("Violet", 0x7A45CB, 0xB780FF, 0x9885AC, 0xA894C3, 3, 3),
            new Palette("Lime", 0x6A8516, 0xBBD943, 0x959D76, 0xADB98B, 2, 2)
        };

        static Color Rgb(int value) { return Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255); }
        static Palette Find(string value)
        {
            string name = value == null ? "" : value.Trim();
            // Teal is the visible name. Original remains the stable storage ID
            // used by released profiles, history and palette metadata.
            if (String.Equals(name, "Teal", StringComparison.OrdinalIgnoreCase)) name = "Original";
            foreach (Palette palette in palettes)
                if (String.Equals(palette.Name, name, StringComparison.OrdinalIgnoreCase)) return palette;
            return palettes[0];
        }
        internal static string AccentStorageName(string value) { return Find(value).Name; }
        internal static string NormalizeAccent(string value) { return AccentStorageName(value); }
        internal static string AccentLabel(string value)
        { string stored = AccentStorageName(value); return stored == "Original" ? "Teal" : stored; }
        internal static string[] AccentLabels
        {
            get {
                var names = new string[palettes.Length];
                for (int index = 0; index < names.Length; index++) names[index] = AccentLabel(palettes[index].Name);
                return names;
            }
        }
        internal static string[] AccentFamilyLabels { get { return (string[])families.Clone(); } }
        internal static string AccentFamily(string preset) { return families[Find(preset).Family]; }
        internal static string[] AccentFamilyPresets(string family)
        {
            int familyIndex = Array.IndexOf(families, family);
            var members = new List<Palette>();
            foreach (Palette palette in palettes) if (palette.Family == familyIndex) members.Add(palette);
            members.Sort(delegate(Palette first, Palette second) { return first.FamilyOrder.CompareTo(second.FamilyOrder); });
            var names = new string[members.Count];
            for (int index = 0; index < names.Length; index++) names[index] = members[index].Name;
            return names;
        }
        internal static FocusIndicatorMode NormalizeStyle(int value)
        { return value == 1 ? FocusIndicatorMode.MutedFrame : value == 2 ? FocusIndicatorMode.InsetEdge : FocusIndicatorMode.Off; }
        // Keep old persisted values readable without rewriting a profile merely
        // by opening the app. Both former enabled choices use the single frame.
        internal static FocusIndicatorMode FrameStyle(int value)
        { return NormalizeStyle(value) == FocusIndicatorMode.Off ? FocusIndicatorMode.Off : FocusIndicatorMode.InsetEdge; }
        internal static string FrameLabel(int value)
        { return FrameStyle(value) == FocusIndicatorMode.Off ? "Off" : "Focus frame"; }
        internal static FocusFrameFinish NormalizeFinish(int value)
        { return value == 1 ? FocusFrameFinish.AccentOutline : FocusFrameFinish.MutedEdge; }
        internal static string[] FinishLabels { get { return new[] { "Muted edge", "Accent outline" }; } }
        internal static string FinishLabel(int value) { return FinishLabels[(int)NormalizeFinish(value)]; }
        internal static string[] Presets
        {
            get {
                var names = new string[palettes.Length];
                for (int index = 0; index < names.Length; index++) names[index] = palettes[index].Name;
                return names;
            }
        }
        internal static string[] Labels { get { return new[] { "Off", "Muted frame", "Inset edge" }; } }
        internal static string[] LightingLabels { get { return new[] { "None", "Soft glow", "Illuminated", "Energy flow" }; } }
        internal static int NormalizeLighting(int value) { return value >= 0 && value <= 3 ? value : -1; }
        internal static FocusLightingMode ResolveLighting(int value, bool legacyGlow)
        {
            int normalized = NormalizeLighting(value);
            return normalized < 0 ? (legacyGlow ? FocusLightingMode.SubtleGlow : FocusLightingMode.None) : (FocusLightingMode)normalized;
        }
        internal static Color Accent(string preset, bool dark)
        { Palette palette = Find(preset); return dark ? palette.DarkAccent : palette.LightAccent; }
        internal static Color Neutral(bool dark) { return dark ? Rgb(0x535B65) : Rgb(0xC8CDD4); }
        internal static Color Frame(string preset, bool dark)
        { Palette palette = Find(preset); return Blend(dark ? palette.DarkFrame : palette.LightFrame, Neutral(dark), 55); }
        internal static Color Inset(string preset, bool dark)
        { Palette palette = Find(preset); return Blend(dark ? palette.DarkFrame : palette.LightFrame, Neutral(dark), 75); }
        static Color Blend(Color tint, Color neutral, int percent)
        {
            return Color.FromArgb((tint.R * percent + neutral.R * (100 - percent) + 50) / 100,
                (tint.G * percent + neutral.G * (100 - percent) + 50) / 100,
                (tint.B * percent + neutral.B * (100 - percent) + 50) / 100);
        }
    }
}
