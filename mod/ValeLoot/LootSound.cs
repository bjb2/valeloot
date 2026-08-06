using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ValeLoot;

/// <summary>
/// The noise a rule makes when loot is PICKED UP.
///
/// ## Where an arrival comes from
///
/// From <see cref="InventoryWatch"/>, which diffs the player's inventory data by item uid on the
/// per-frame main-thread tick. So the trigger is the item landing in the bag, whether the panel is
/// open, closed, or showing some other page — not a repaint, and not the first time a cell happened
/// to be bound. The judging is <see cref="InventoryPaint.Judge"/>, the same evaluator that picks the
/// colour, so the noise and the highlight can never name different rules.
///
/// The very first observation of a bag is SILENT, and that is load-bearing: on that pass every item
/// you own looks new, and forty pings when you log in would teach the player to turn sound off within
/// a minute. The watcher holds that baseline and re-primes it on a character change; this file is
/// handed only what actually arrived.
///
/// One batch is one sound, not one per item, and the interval below is a floor under how often any
/// of it can happen: ten things landing at once is a chime, and a rule that fires on everything
/// cannot turn into a siren.
///
/// ## Why WAV files on disk rather than Unity audio
///
/// Playing through the game's own audio graph means constructing an `AudioClip` and an `AudioSource`
/// through raw il2cpp, with a managed PCM callback the runtime calls on the audio thread — a lot of
/// ABI surface for a beep, and this project has already crashed a game process by getting one engine
/// call's shape wrong. `PlaySound` from `winmm` takes a filename and a flag and plays asynchronously.
/// It cannot deadlock the render thread and it cannot take the game down.
///
/// The cost is that it is Windows-only. The game ships on Windows, and a missing `winmm` disables
/// sound and logs once rather than throwing — the highlights, which are the point, are untouched.
///
/// ## Why the built-in sounds are synthesised on first run
///
/// So a fresh install has usable sounds and this repository ships no binary assets: five short tones
/// are written into the sounds directory the first time the mod runs, as ordinary WAV files. They are
/// yours after that — overwrite `chime.wav` with anything you like and the filter line does not change.
/// </summary>
internal static class LootSound
{
    public const string DirectoryName = "valeloot-sounds";

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFilename = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundW(string? sound, IntPtr module, uint flags);

    /// <summary>Silence between sounds. A stack of loot landing at once should ping once, not forty times.</summary>
    private const int MinIntervalMs = 250;

    private static string _directory = "";
    private static Action<string> _log = _ => { };
    private static bool _available;
    private static long _lastPlayTicks;

    public static bool Enabled = true;
    public static long Played;
    public static long Suppressed;
    /// <summary>Items announced, which is not the same as noises made — a batch is one noise.</summary>
    public static long Announced;
    /// <summary>The last item that made a noise, for `status`. Empty until one does.</summary>
    public static string LastUid = "";

    public static string Directory => _directory;

    /// <summary>Create the sounds directory, write the built-ins if missing, and check winmm answers.</summary>
    public static void Install(string configDirectory, Action<string> log)
    {
        _log = log;
        _directory = Path.Combine(configDirectory, DirectoryName);
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            foreach ((string name, short[] samples) in BuiltIns())
            {
                string path = Path.Combine(_directory, name + ".wav");
                // Never overwrite: the player's own file wins, for ever, including after an upgrade.
                if (!File.Exists(path)) File.WriteAllBytes(path, Wav(samples));
            }
            _available = true;
            log($"sounds ready in {_directory}");
        }
        catch (Exception e)
        {
            _available = false;
            log($"sounds disabled — could not prepare {_directory}: {e.Message}");
        }
    }

    /**
     * The items just picked up that a rule claimed with a `Sound`, handed over as one batch.
     *
     * A batch rather than an item at a time because ten things landing at once is one chime, not ten:
     * "which sound wins when three arrive together" is decided here, in one place, and the answer is
     * the first in the batch — which is bag-walk order, the same order the paint pass would have lit
     * them in. Every other item in the batch is still counted, so the difference between
     * <see cref="Announced"/> and <see cref="Played"/> says how much landed quietly.
     *
     * There is no dedupe here on purpose. The uid baseline lives in <see cref="InventoryWatch"/> and
     * is exactly what is in the bag right now, so an item that leaves and comes back is an arrival
     * again. A second set in here could only ever disagree with that one.
     */
    public static void Arrivals(List<(string Uid, string Sound)> pickups)
    {
        if (pickups.Count == 0) return;

        Announced += pickups.Count;
        (string uid, string sound) = pickups[0];
        LastUid = uid;
        Play(sound);
    }

    public static void Play(string name)
    {
        if (!Enabled || !_available) return;

        long now = DateTime.UtcNow.Ticks;
        if ((now - _lastPlayTicks) / TimeSpan.TicksPerMillisecond < MinIntervalMs) { Suppressed++; return; }
        _lastPlayTicks = now;

        string path = Path.Combine(_directory, name + ".wav");
        if (!File.Exists(path))
        {
            // Named in a filter but absent from disk. Logged once per name would need another set to
            // track; the count in `status` is enough, and the load summary already lists the names.
            Suppressed++;
            return;
        }

        try
        {
            // `PlaySoundW` returns false when it could not play — a busy device, an unreadable file, a
            // WAV the mixer will not take. Counting that as played would make the `status` counter a
            // statement about calls made rather than about noises heard, which is the wrong question.
            if (PlaySoundW(path, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault)) Played++;
            else Suppressed++;
        }
        catch (Exception e)
        {
            // A missing winmm (or a locked audio device) disables sound for the session rather than
            // throwing inside a UI repaint. The highlights are the feature; this is the garnish.
            _available = false;
            _log($"sounds disabled — {e.Message}");
        }
    }

    public static string StatusJson()
        => "{\"kind\":\"sound\",\"available\":" + (_available ? "true" : "false")
         + ",\"enabled\":" + (Enabled ? "true" : "false")
         + ",\"played\":" + Played
         + ",\"suppressed\":" + Suppressed
         + ",\"announced\":" + Announced
         + ",\"last\":" + JsonString(LastUid)
         + "}";

    /// <summary>A uid is game data, so it is escaped rather than trusted to be JSON-safe.</summary>
    private static string JsonString(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') builder.Append('\\').Append(c);
            else if (c < ' ') builder.Append(' ');
            else builder.Append(c);
        }
        return builder.Append('"').ToString();
    }

    private const int SampleRate = 44100;

    /**
     * The five built-in tones, as raw 16-bit mono samples.
     *
     * Deliberately short and deliberately different in PITCH rather than in volume: the player has to
     * tell them apart through game audio, and a louder version of the same beep is not a second sound.
     */
    private static IEnumerable<(string Name, short[] Samples)> BuiltIns()
    {
        yield return ("blip", Tone(880, 0.06, 0.010));
        yield return ("chime", Mix(Tone(880, 0.25, 0.015), Tone(1318, 0.25, 0.015, delay: 0.06)));
        yield return ("ding", Tone(1568, 0.35, 0.008));
        yield return ("alert", Mix(Tone(660, 0.10, 0.004), Tone(660, 0.10, 0.004, delay: 0.16)));
        yield return ("thud", Tone(120, 0.18, 0.006));
    }

    /**
     * One decaying sine.
     *
     * The attack ramp is not decoration: a sine that starts at full amplitude begins with a
     * discontinuity, and the click that produces is louder and nastier than the tone itself.
     */
    private static short[] Tone(double hz, double seconds, double attack, double delay = 0)
    {
        int offset = (int)(delay * SampleRate);
        int length = (int)(seconds * SampleRate);
        var samples = new short[offset + length];
        int attackSamples = Math.Max(1, (int)(attack * SampleRate));
        for (int i = 0; i < length; i++)
        {
            double t = i / (double)SampleRate;
            double envelope = Math.Exp(-3.5 * (i / (double)length));
            if (i < attackSamples) envelope *= i / (double)attackSamples;
            samples[offset + i] = (short)(Math.Sin(2 * Math.PI * hz * t) * envelope * 9000);
        }
        return samples;
    }

    private static short[] Mix(short[] a, short[] b)
    {
        var result = new short[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < result.Length; i++)
        {
            int sum = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
            result[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
        }
        return result;
    }

    /// <summary>Wrap samples in a canonical 44-byte RIFF/WAVE header — 16-bit mono PCM.</summary>
    private static byte[] Wav(short[] samples)
    {
        int dataBytes = samples.Length * 2;
        var stream = new MemoryStream(44 + dataBytes);
        var writer = new BinaryWriter(stream);
        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataBytes);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);                       // PCM chunk size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);           // byte rate
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataBytes);
        foreach (short sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }
}
