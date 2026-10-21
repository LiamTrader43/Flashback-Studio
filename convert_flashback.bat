@echo off
rem convert_flashback.bat - turns Flashback clips (.flb) into MP4 videos.
rem Part of Flashback-Studio: https://github.com/LiamTrader43/Flashback-Studio
rem
rem Usage:
rem   - Drag one or more clip_*.flb files onto this file, or
rem   - Double-click it to convert every clip in the same folder as this file.
rem
rem Needs ffmpeg (https://ffmpeg.org) on PATH. Clips that already have an .mp4
rem are skipped. Clips recorded with audio get it as an AAC track. Clips saved
rem by older Flashback builds (.raw + .txt) work too.
rem
rem .flb clips are compressed in Flashback's own format, which only this
rem converter can read: the PowerShell part at the end of this file decodes
rem them and streams the frames to ffmpeg.

setlocal

where ffmpeg >nul 2>nul
if errorlevel 1 (
    echo ffmpeg was not found. Install it from https://ffmpeg.org and add its bin folder to PATH.
    pause
    exit /b 1
)

set "FB_SELF=%~f0"
set "FB_ARGS=%*"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=[IO.File]::ReadAllText($env:FB_SELF); $i=$s.IndexOf('FLASHBACK_PS'+'_BELOW'); iex $s.Substring($s.IndexOf([char]10,$i)+1)"

echo.
pause
exit /b 0

rem ---- Everything below is the PowerShell converter. FLASHBACK_PS_BELOW
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Diagnostics;
using System.Text;

// Reader for Flashback clip files (.flb). Version 1, as recorded, is documented
// next to flashbackSaveClipFile in sysmodules/rosalina/source/flashback.c of
// Luma3DS-Flashback (https://github.com/LiamTrader43/Luma3DS-Flashback):
//   [ring slots]  slot i at offset i * slotStride: a compressed frame, or the
//                 raw frame if its stored size is slotBytes
//   [index]       entryCount x { u32 slot, u32 size, u32 copies }, oldest first
//   [trailer]     the last 48 bytes: magic "FLBK", version, width, height, fps,
//                 slotStride, slotBytes, entryCount, frameCount, indexOffset
// Version 2 is a clip compacted by the Flashback Studio 3DS app (see its
// 3ds-app/source/flb.h): the frames back to back, and each index entry's first
// field is the frame's offset in the file instead of its slot (slotStride is 0).
// Version 3 is a clip with audio (recorded, or compacted): index entries are
// { u32 offset, u32 size, u32 copies, u32 audioPos } (audioPos: the audio
// sample at which the frame is first shown), followed by 32 bytes of audio
// info: { u32 sampleRate (mHz), u16 channels, u16 bits, u32 offset,
// u32 capacity, u32 first, u32 end, u32 missed, u32 reserved }; stereo PCM16
// sample i is at offset + (i % capacity) * 4, and first .. end - 1 are held.
public static class FlashbackClip
{
    const uint Magic = 0x4B424C46;  // "FLBK"
    const int TrailerSize = 48;

    public class Info
    {
        public int Width, Height, Fps, Frames;
        public bool HasAudio;
        public long AudioRateMhz, AudioOffset, AudioCapacity, AudioFirst, AudioEnd, AudioStart;
    }

    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }

    static void ReadFully(Stream s, byte[] buf, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, got, len - got);
            if (n <= 0) throw new Exception("the file ends early (was it copied completely?)");
            got += n;
        }
    }

    // Port of flashbackDecompress: QOI-style ops on RGB565 pixels.
    static bool Unpack(byte[] src, int len, ushort[] dst, int pixels)
    {
        ushort[] index = new ushort[64];
        ushort px = 0;
        int p = 0, i = 0;
        while (i < pixels)
        {
            if (p >= len) return false;
            int b = src[p++];
            if (b == 0xFE)
            {
                if (len - p < 2) return false;
                px = (ushort)(src[p] | (src[p + 1] << 8));
                p += 2;
            }
            else if ((b & 0xC0) == 0xC0)
            {
                int run = (b & 0x3F) + 1;
                if (run > pixels - i) return false;
                while (run-- > 0) dst[i++] = px;
                continue;
            }
            else if ((b & 0xC0) == 0x00)
            {
                px = index[b];
                dst[i++] = px;
                continue;
            }
            else
            {
                int dr, dg, db;
                if ((b & 0xC0) == 0x40)
                {
                    dr = ((b >> 4) & 3) - 2;
                    dg = ((b >> 2) & 3) - 2;
                    db = (b & 3) - 2;
                }
                else
                {
                    if (p >= len) return false;
                    int b2 = src[p++];
                    dg = (b & 0x3F) - 32;
                    dr = ((b2 >> 4) - 8) + (dg >> 1);
                    db = ((b2 & 0xF) - 8) + (dg >> 1);
                }
                int r = ((px >> 11) + dr) & 0x1F;
                int g = (((px >> 5) & 0x3F) + dg) & 0x3F;
                int bl = ((px & 0x1F) + db) & 0x1F;
                px = (ushort)((r << 11) | (g << 5) | bl);
            }
            index[(int)(unchecked((uint)px * 0x9E3779B1u) >> 26)] = px;
            dst[i++] = px;
        }
        return p == len;
    }

    // Decodes every frame of a clip, oldest first, handing each one (RGB565
    // little-endian, top row first) to 'sink' along with how many times it repeats.
    public static Info Read(string path, Action<byte[], int> sink)
    {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            long length = fs.Length;
            if (length < TrailerSize) throw new Exception("not a Flashback clip (file too small)");

            byte[] t = new byte[TrailerSize];
            fs.Seek(length - TrailerSize, SeekOrigin.Begin);
            ReadFully(fs, t, TrailerSize);
            if (U32(t, 0) != Magic) throw new Exception("not a Flashback clip (or the copy is incomplete)");
            uint version = U32(t, 4);
            if (version < 1 || version > 3) throw new Exception("made by a newer Flashback version; get the latest convert_flashback.bat");
            int entrySize = version == 3 ? 16 : 12;
            int audioInfoSize = version == 3 ? 32 : 0;

            int width = (int)U32(t, 8), height = (int)U32(t, 12), fps = (int)U32(t, 16);
            long stride = U32(t, 20);
            int slotBytes = (int)U32(t, 24), entries = (int)U32(t, 28), frames = (int)U32(t, 32);
            long indexOffset = U32(t, 36);
            if (width <= 0 || height <= 0 || width > 800 || height > 480 || slotBytes != width * height * 2 ||
                fps <= 0 || fps > 60 || (version == 1 && stride < slotBytes) || entries < 0 ||
                indexOffset + (long)entries * entrySize + audioInfoSize + TrailerSize != length)
                throw new Exception("the clip's header is damaged");

            byte[] idx = new byte[entries * entrySize + audioInfoSize];
            fs.Seek(indexOffset, SeekOrigin.Begin);
            ReadFully(fs, idx, idx.Length);

            Info info = new Info();
            info.Width = width; info.Height = height; info.Fps = fps; info.Frames = frames;
            if (version == 3)
            {
                int a = entries * entrySize;
                info.HasAudio = true;
                info.AudioRateMhz = U32(idx, a);
                info.AudioOffset = U32(idx, a + 8);
                info.AudioCapacity = U32(idx, a + 12);
                info.AudioFirst = U32(idx, a + 16);
                info.AudioEnd = U32(idx, a + 20);
                info.AudioStart = entries > 0 ? U32(idx, 12) : 0;
                if (BitConverter.ToUInt16(idx, a + 4) != 2 || BitConverter.ToUInt16(idx, a + 6) != 16 ||
                    info.AudioRateMhz < 8000000 || info.AudioRateMhz > 96000000 || info.AudioCapacity == 0 ||
                    info.AudioOffset + info.AudioCapacity * 4 > indexOffset || info.AudioFirst > info.AudioEnd ||
                    info.AudioEnd - info.AudioFirst > info.AudioCapacity)
                    throw new Exception("the clip's audio header is damaged");
            }

            int pixels = width * height;
            byte[] stored = new byte[slotBytes];
            ushort[] decoded = new ushort[pixels];
            byte[] frame = new byte[slotBytes];
            long total = 0;

            for (int e = 0; e < entries; e++)
            {
                long where = U32(idx, e * entrySize);     // version 1: ring slot, versions 2 and 3: offset
                long offset = version == 1 ? where * stride : where;
                int size = (int)U32(idx, e * entrySize + 4);
                int copies = (int)U32(idx, e * entrySize + 8);
                if (size <= 0 || size > slotBytes || copies <= 0 || offset + size > indexOffset)
                    throw new Exception(string.Format("frame entry {0} is damaged", e));

                fs.Seek(offset, SeekOrigin.Begin);
                ReadFully(fs, stored, size);
                if (size == slotBytes)
                    Buffer.BlockCopy(stored, 0, frame, 0, slotBytes);
                else
                {
                    if (!Unpack(stored, size, decoded, pixels))
                        throw new Exception(string.Format("frame {0} is damaged", e));
                    Buffer.BlockCopy(decoded, 0, frame, 0, slotBytes);
                }
                sink(frame, copies);
                total += copies;
            }

            if (total != frames) throw new Exception("the clip's frame count doesn't match its index");
            return info;
        }
    }

    static void Put32(byte[] b, int o, long v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static void Put16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }

    // Writes the clip's audio, as long as the clip, to a WAV file: from the
    // sample at which the first frame is shown, frames / fps seconds of it.
    // Samples the clip doesn't hold (before recording started, ...) are silence.
    public static void WriteWav(string path, Info info, string wav)
    {
        double rate = info.AudioRateMhz / 1000.0;
        long count = (long)Math.Round(info.Frames * rate / info.Fps);
        byte[] header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        Put32(header, 4, 36 + count * 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        Put32(header, 16, 16);
        Put16(header, 20, 1);                       // PCM
        Put16(header, 22, 2);                       // stereo
        Put32(header, 24, (long)Math.Round(rate));
        Put32(header, 28, (long)Math.Round(rate) * 4);
        Put16(header, 32, 4);
        Put16(header, 34, 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        Put32(header, 40, count * 4);

        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        using (FileStream o = new FileStream(wav, FileMode.Create, FileAccess.Write))
        {
            o.Write(header, 0, header.Length);
            byte[] buf = new byte[65536 * 4];
            for (long done = 0; done < count; )
            {
                int n = (int)Math.Min(count - done, 65536);
                Array.Clear(buf, 0, n * 4);
                // the part of these samples the clip holds, in at most two pieces (the ring wraps)
                long a = Math.Max(info.AudioStart + done, info.AudioFirst);
                long b = Math.Min(info.AudioStart + done + n, info.AudioEnd);
                while (a < b)
                {
                    long pos = a % info.AudioCapacity;
                    long k = Math.Min(b - a, info.AudioCapacity - pos);
                    fs.Seek(info.AudioOffset + pos * 4, SeekOrigin.Begin);
                    ReadAt(fs, buf, (int)(a - info.AudioStart - done) * 4, (int)k * 4);
                    a += k;
                }
                o.Write(buf, 0, n * 4);
                done += n;
            }
        }
    }

    static void ReadAt(Stream s, byte[] buf, int at, int len)
    {
        int got = 0;
        while (got < len)
        {
            int n = s.Read(buf, at + got, len - got);
            if (n <= 0) throw new Exception("the file ends early (was it copied completely?)");
            got += n;
        }
    }

    // Decodes a clip straight into ffmpeg, which writes the MP4 (with the
    // clip's audio, from a temporary WAV file next to it).
    public static Info ToMp4(string path, string mp4)
    {
        Info info = Read(path, delegate(byte[] f, int c) { });   // validate everything before starting ffmpeg

        string wav = info.HasAudio ? mp4 + ".audio.wav" : null;
        string audioIn = "", audioOut = "";
        if (wav != null)
        {
            WriteWav(path, info, wav);
            audioIn = "-i \"" + wav + "\" -map 0:v -map 1:a ";
            audioOut = "-c:a aac -b:a 192k -ar 48000 ";
        }

        ProcessStartInfo psi = new ProcessStartInfo("ffmpeg", string.Format(
            "-hide_banner -loglevel error -n -f rawvideo -pixel_format rgb565le -video_size {0}x{1} -framerate {2} -i - {4}" +
            "-vf \"scale=iw*2:ih*2:flags=neighbor\" -c:v libx264 -crf 12 -pix_fmt yuv420p {5}\"{3}\"",
            info.Width, info.Height, info.Fps, mp4, audioIn, audioOut));
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;

        // .NET wraps ffmpeg's input in a text writer using the console's input
        // encoding; when that's UTF-8 it writes a 3-byte byte-order mark first,
        // which would shift every frame. Start ffmpeg with BOM-less UTF-8.
        Encoding savedEncoding = Console.InputEncoding;
        Process started;
        try { Console.InputEncoding = new UTF8Encoding(false); } catch (Exception) { }
        try { started = Process.Start(psi); }
        finally { try { Console.InputEncoding = savedEncoding; } catch (Exception) { } }

        using (Process p = started)
        {
            Stream stdin = p.StandardInput.BaseStream;
            try
            {
                Read(path, delegate(byte[] f, int copies)
                {
                    for (int c = 0; c < copies; c++)
                        stdin.Write(f, 0, f.Length);
                });
            }
            catch (IOException) { }     // ffmpeg stopped early; its exit code says why
            finally { try { stdin.Close(); } catch (IOException) { } }
            p.WaitForExit();
            if (wav != null)
                try { File.Delete(wav); } catch (Exception) { }
            if (p.ExitCode != 0)
            {
                try { File.Delete(mp4); } catch (Exception) { }
                throw new Exception("ffmpeg failed (exit code " + p.ExitCode + ")");
            }
        }
        return info;
    }

    // Decodes a clip into a raw RGB565 file (frames back to back), e.g. for other tools.
    public static Info ToRaw(string path, string rawOut)
    {
        using (FileStream o = new FileStream(rawOut, FileMode.Create, FileAccess.Write))
        {
            return Read(path, delegate(byte[] f, int copies)
            {
                for (int c = 0; c < copies; c++)
                    o.Write(f, 0, f.Length);
            });
        }
    }
}
'@

function Get-Reason($err)
{
    $e = $err.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    return $e.Message
}

$here = Split-Path -Parent $env:FB_SELF
if ([string]::IsNullOrWhiteSpace($env:FB_ARGS)) {
    # .flb clips, plus .raw clips from older builds (those always have a .txt next to them).
    $files = @(Get-ChildItem -LiteralPath $here -File |
        Where-Object { $_.Name -like 'clip_*' -and ($_.Extension -eq '.flb' -or
                       ($_.Extension -eq '.raw' -and (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($_.FullName, '.txt'))))) } |
        ForEach-Object { $_.FullName })
} else {
    $files = @([regex]::Matches($env:FB_ARGS, '"([^"]+)"|(\S+)') |
        ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } })
}

if ($files.Count -eq 0) {
    Write-Host "No Flashback clips (clip_*.flb) found next to this file."
    Write-Host "Drag clips onto convert_flashback.bat, or put it in the folder with your clips."
}

$converted = 0; $skipped = 0; $failed = 0
foreach ($f in $files) {
    $name = [IO.Path]::GetFileName($f)
    $ext = [IO.Path]::GetExtension($f).ToLowerInvariant()
    $mp4 = [IO.Path]::ChangeExtension($f, '.mp4')
    $mp4Name = [IO.Path]::GetFileName($mp4)

    if ($ext -ne '.flb' -and $ext -ne '.raw') {
        Write-Host "Skipping ${name}: not a Flashback clip (.flb)."
        $skipped++; continue
    }
    if (Test-Path -LiteralPath $mp4) {
        Write-Host "Skipping ${name}: $mp4Name already exists."
        $skipped++; continue
    }

    Write-Host "Converting $name..."
    try {
        if ($ext -eq '.flb') {
            $info = [FlashbackClip]::ToMp4($f, $mp4)
            Write-Host ("  Saved {0} ({1}x{2}, {3} fps, {4} frames{5})" -f $mp4Name, $info.Width, $info.Height, $info.Fps, $info.Frames,
                        $(if ($info.HasAudio) { ", with audio" } else { "" }))
        } else {
            # Clips from older Flashback builds: raw frames, described by a .txt.
            $txt = [IO.Path]::ChangeExtension($f, '.txt')
            if (-not (Test-Path -LiteralPath $txt)) { throw "its .txt is missing, so its resolution and frame rate are unknown" }
            $t = Get-Content -LiteralPath $txt -Raw
            $res = [regex]::Match($t, 'Resolution:\s+(\d+x\d+)').Groups[1].Value
            $fps = [regex]::Match($t, 'Frame rate:\s+(\d+)').Groups[1].Value
            if (-not $res -or -not $fps) { throw "couldn't read the resolution or frame rate from its .txt" }
            & ffmpeg -hide_banner -loglevel error -n -f rawvideo -pixel_format rgb565le -video_size $res -framerate $fps -i $f -vf "scale=iw*2:ih*2:flags=neighbor" -c:v libx264 -crf 12 -pix_fmt yuv420p $mp4
            if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed (exit code $LASTEXITCODE)" }
            Write-Host "  Saved $mp4Name"
        }
        $converted++
    } catch {
        Write-Host "  Failed: $(Get-Reason $_)" -ForegroundColor Red
        $failed++
    }
}

Write-Host ""
Write-Host "Done: $converted converted, $skipped skipped, $failed failed."
