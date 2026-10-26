using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FlashbackStudio
{
    // Clip names, and how sizes, lengths and times are written: as the 3DS app
    // does (see main.c), so the two read the same.
    static class Names
    {
        static readonly Regex Dated = new Regex(@"^clip_(\d{1,4})-(\d{1,2})-(\d{1,2})_(\d{1,2})-(\d{1,2})-(\d{1,2})");

        // Named with its date, as Flashback saves clips ("clip_2026-10-20_19-13-05.123.flb")?
        public static bool IsDated(string name) => Dated.IsMatch(name);

        // Clips named with their date newest first, then any renamed ones in name order.
        public static int Compare(string a, string b)
        {
            bool da = IsDated(a), db = IsDated(b);
            if (da != db)
                return da ? -1 : 1;
            return da ? string.CompareOrdinal(b, a) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // "clip_2026-10-20_19-13-05.123.flb" -> "2026-10-20 19:13:05"; other names
        // lose their extension.
        public static string Pretty(string name)
        {
            Match m = Dated.Match(name);
            if (m.Success)
                return $"{int.Parse(m.Groups[1].Value):0000}-{int.Parse(m.Groups[2].Value):00}-{int.Parse(m.Groups[3].Value):00} " +
                       $"{int.Parse(m.Groups[4].Value):00}:{int.Parse(m.Groups[5].Value):00}:{int.Parse(m.Groups[6].Value):00}";
            return Path.GetFileNameWithoutExtension(name);
        }

        // A tile's first line: when the clip was saved (or its name).
        public static string TileTime(string name)
        {
            Match m = Dated.Match(name);
            if (m.Success)
                return $"{int.Parse(m.Groups[4].Value):00}:{int.Parse(m.Groups[5].Value):00}:{int.Parse(m.Groups[6].Value):00}";
            return Pretty(name);
        }

        // Decimal megabytes, like the Flashback menu: one decimal below 10 MB.
        public static string Size(long bytes)
        {
            long tenths = (bytes + 50000) / 100000;
            return tenths < 100 ? $"{tenths / 10}.{tenths % 10} MB" : $"{(tenths + 5) / 10} MB";
        }

        // m:ss
        public static string Length(int frames, int fps)
        {
            int secs = (frames + fps / 2) / fps;
            return $"{secs / 60}:{secs % 60:00}";
        }

        // m:ss.t
        public static string Time(int frame, int fps)
        {
            long tenths = (long)frame * 10 / fps;
            return $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}";
        }

        public static string Resolution(int width, int height, int fps, bool sound)
        {
            string size = width == 400 && height == 240 ? "Full resolution (400x240)" :
                          width == 200 && height == 120 ? "Half resolution (200x120)" : $"{width}x{height}";
            return $"{size}, {fps} fps" + (sound ? ", with sound" : "");
        }

        // The file name for a typed name: each space made a dash, and ".flb"
        // added (unless it was typed too).
        public static string ClipFileName(string typed)
        {
            if (typed.EndsWith(".flb", StringComparison.OrdinalIgnoreCase))
                typed = typed.Substring(0, typed.Length - 4);
            return typed.Replace(' ', '-') + ".flb";
        }

        // Windows reserves these names (before the first dot, whatever the case)
        // for devices.
        static bool IsReserved(string name)
        {
            string stem = name.Split('.')[0].ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                   (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
        }

        // Why 'typed' can't be the new name of clip 'current' in 'folder' (the 3DS
        // app's rules, so a clip can go back and forth), or null if it can.
        public static string CheckRename(string typed, string folder, string current)
        {
            string name = ClipFileName(typed), stem = name.Substring(0, name.Length - 4);
            foreach (char ch in stem)
                if (ch < 0x20 || ch > 0x7E || "\\/:*?\"<>|".IndexOf(ch) >= 0)
                    return "Use letters, numbers and punctuation other than \\ / : * ? \" < > |. (Spaces become dashes.)";
            if (stem.Length == 0 || stem.EndsWith("."))
                return "The name can't be empty or end with a dot.";
            if (stem.Length > 64)
                return "The name can't be longer than 64 characters.";
            if (IsReserved(name))
                return "Windows can't use that name (CON, PRN, AUX, NUL, COM0-9 and LPT0-9 are taken). Try another.";
            if (!string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, name)))
                return "There's already a clip with that name.";
            return null;
        }

        // A free name for a copy of clip 'name' in 'folder': "<name>-trim<ext>",
        // or "<name>-trim2<ext>" and so on if that's taken. Null if none is free.
        public static string FreeName(string folder, string name, string ext)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length > 80)
                stem = stem.Substring(0, 80);
            for (int n = 1; n < 100; n++)
            {
                string candidate = stem + (n == 1 ? "-trim" : $"-trim{n}") + ext;
                if (!File.Exists(Path.Combine(folder, candidate)))
                    return candidate;
            }
            return null;
        }
    }
}
