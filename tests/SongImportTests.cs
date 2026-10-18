using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using XboxRumble;

internal static class SongImportTests
{
    private static int checks;
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void U32(BinaryWriter writer, int value) { writer.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
    private static void Track(BinaryWriter writer, byte[] bytes) { writer.Write(Encoding.ASCII.GetBytes("MTrk")); U32(writer, bytes.Length); writer.Write(bytes); }
    private static double Voice(Melody song, double beat) { double l, r; song.Sample(beat, out l, out r); return r; }
    private static void Reject(Action load, string reason)
    {
        bool failed = false;
        try { load(); } catch (InvalidDataException) { failed = true; } catch (EndOfStreamException) { failed = true; }
        Assert(failed, reason);
    }
    public static int Main(string[] args)
    {
        try
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "song-fixtures"); Directory.CreateDirectory(directory);
            string midi = Path.Combine(directory, "Original two-note test.mid");
            using (BinaryWriter writer = new BinaryWriter(File.Create(midi)))
            {
                writer.Write(Encoding.ASCII.GetBytes("MThd")); U32(writer, 6); writer.Write(new byte[] { 0,1,0,3,0,96 });
                Track(writer, new byte[] { 0,255,81,3,7,161,32, 96,255,81,3,15,66,64, 96,255,47,0 });
                Track(writer, new byte[] { 0,255,3,4,76,101,97,100, 0,144,60,100, 48,60,0, 48,144,67,100, 96,128,67,0, 0,255,47,0 });
                Track(writer, new byte[] { 0,153,80,100, 96,137,80,0, 0,255,47,0 });
            }
            Melody[] loaded = MidiImport.Load(midi);
            Assert(loaded.Length == 1, "MIDI conductor and drum channels excluded");
            Melody melody = loaded[0];
            Assert(melody.DefaultBpm == 120 && Math.Abs(melody.Beats - 4) < 1e-9, "Tempo changes convert into correct playback time");
            Assert(Math.Abs(Voice(melody, 0.2) - Math.Pow(2, -7.0/12)) < 0.001, "MIDI first pitch mapped correctly");
            Assert(Voice(melody, 0.75) == 0, "Velocity-zero running-status note off preserves rest");
            Assert(Voice(melody, 1.5) == 1 && Voice(melody, 2.5) == 1, "Second note respects slower tempo map");
            Assert(Voice(melody, 3.2) == 0, "MIDI ends with a silent loop gap");
            string empty = Path.Combine(directory, "Tempo only.mid");
            using (BinaryWriter writer = new BinaryWriter(File.Create(empty)))
            {
                writer.Write(Encoding.ASCII.GetBytes("MThd")); U32(writer, 6); writer.Write(new byte[] { 0,1,0,1,1,224 });
                Track(writer, new byte[] { 0,255,81,3,4,171,80, 0,255,88,4,4,2,24,8, 0,255,47,0 });
            }
            string emptyReason = "";
            try { MidiImport.Load(empty); } catch (InvalidDataException problem) { emptyReason = problem.Message; }
            Assert(emptyReason.Contains("没有音符") && emptyReason.Contains("重新导出"), "Conductor-only export reports missing notes with a useful remedy");
            string fastPath = Path.Combine(directory, "300 BPM.mid");
            byte[] fastBytes = File.ReadAllBytes(midi);
            fastBytes[26] = 3; fastBytes[27] = 13; fastBytes[28] = 64; File.WriteAllBytes(fastPath, fastBytes);
            Melody fast = MidiImport.Load(fastPath)[0];
            Assert(fast.DefaultBpm == 300 && Math.Abs(fast.Beats - 7) < 1e-9, "MIDI tempo at 300 is preserved along with subsequent tempo changes");
            string tempoCache = Path.Combine(directory, "tempo.rumble"); fast.Save(tempoCache);
            Assert(Melody.Load(tempoCache).DefaultBpm == 300, "300 BPM persists through score cache");
            new Melody("Slow", new[] { 60 }, new double[] { 1 }, 1).Save(tempoCache);
            Assert(Melody.Load(tempoCache).DefaultBpm == 1, "1 BPM persists through score cache");
            string invalid = Path.Combine(directory, "invalid.mid");
            byte[] bytes = File.ReadAllBytes(midi); File.WriteAllBytes(invalid, new byte[] { 0,1,2 });
            Reject(delegate { MidiImport.Load(invalid); }, "Truncated input rejected");
            bytes[9] = 2; File.WriteAllBytes(invalid, bytes);
            Reject(delegate { MidiImport.Load(invalid); }, "Asynchronous type 2 MIDI rejected explicitly");
            bytes[9] = 1; bytes[12] = 0x80; File.WriteAllBytes(invalid, bytes);
            Reject(delegate { MidiImport.Load(invalid); }, "Unsupported SMPTE timing rejected");

            float[] samples = new float[AudioImport.Rate * 3];
            for (int i = 0; i < samples.Length; i++)
            {
                double time = i / (double)AudioImport.Rate, fraction = time - Math.Floor(time);
                double hz = time < 1 ? 220 : time < 2 ? 261.625565 : 329.627557;
                if (fraction < 0.8) samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * time) + 0.08 * Math.Sin(4 * Math.PI * hz * time));
            }
            Melody audio = AudioImport.Analyze(samples, "Original synthetic notes", CancellationToken.None);
            Assert(audio.IsAudio && audio.DefaultBpm == 120 && Math.Abs(audio.Beats - 6) < 1e-9, "Audio analysis preserves duration at 100 percent");
            Assert(Math.Abs(Voice(audio, 2.4) / Voice(audio, 0.4) - Math.Pow(2, 3.0/12)) < 0.01, "Audio identifies minor third");
            Assert(Math.Abs(Voice(audio, 4.4) / Voice(audio, 0.4) - Math.Pow(2, 7.0/12)) < 0.01, "Audio identifies perfect fifth");
            Assert(Voice(audio, 1.8) == 0, "Audio silence maps to zero rumble");
            Reject(delegate { AudioImport.Analyze(new float[8000], "silence", CancellationToken.None); }, "Silent audio reports no stable pitch");
            bool cancelled = false;
            try { AudioImport.Analyze(samples, "cancel", new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "Analysis respects cancellation");
            SongFiles.SaveImported(Path.Combine(directory, "cache"), new[] { audio, melody });
            string cacheError;
            Melody[] cached = SongFiles.LoadFolder(Path.Combine(directory, "cache"), out cacheError);
            Assert(cached.Length == 2 && cacheError == "", "Imported scores reload without source media");
            foreach (Melody song in cached)
            {
                Melody original = song.IsAudio ? audio : melody;
                Assert(song.Name == original.Name && song.Beats == original.Beats && Voice(song, 0.4) == Voice(original, 0.4), "Cached score preserves timing and output");
            }

            string decoder = AudioImport.FindDecoder();
            if (decoder != null)
            {
                string wav = Path.Combine(directory, "original test.wav"), mp3 = Path.Combine(directory, "original test.mp3");
                using (BinaryWriter writer = new BinaryWriter(File.Create(wav)))
                {
                    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(AudioImport.Rate); writer.Write(AudioImport.Rate * 2); writer.Write((short)2); writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2); foreach (float sample in samples) writer.Write((short)(sample * 32767));
                }
                using (Process encoder = Process.Start(new ProcessStartInfo(decoder, "-nostdin -hide_banner -loglevel error -y -i \"" + wav + "\" -ar 44100 -c:a libmp3lame -b:a 96k \"" + mp3 + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!encoder.WaitForExit(15000)) { encoder.Kill(); throw new Exception("Test MP3 encoding timed out"); }
                    Assert(encoder.ExitCode == 0, "Encode original synthetic test MP3");
                }
                Melody decoded = AudioImport.Load(mp3, CancellationToken.None);
                Assert(decoded.IsAudio && Math.Abs(decoded.Beats - 6) < 0.1, "Decode real MP3 file at correct duration");
                Assert(Math.Abs(Voice(decoded, 4.4) / Voice(decoded, 0.4) - Math.Pow(2, 7.0/12)) < 0.01, "End-to-end MP3 pitch estimate survives codec");
                Assert(Voice(decoded, 1.8) == 0, "MP3 rest survives decoding");
            }
            else Console.WriteLine("SKIP: MP3 codec integration; FFmpeg is not installed. Pitch estimator was tested.");
            if (args.Length == 1 && File.Exists(args[0]))
            {
                Melody[] song = MidiImport.Load(args[0]);
                Assert(song.Length > 0, "Requested song loads");
                foreach (Melody voice in song) Console.WriteLine("Loaded: " + voice.Name + " | " + voice.Detail + " | " + voice.DefaultBpm.ToString("0.0") + " BPM | " + (voice.Beats * 60 / voice.DefaultBpm).ToString("0.0") + " seconds");
                foreach (Melody voice in song)
                    for (int i = 0; i < 1000; i++) { double l, r; voice.Sample(i * voice.Beats / 1000, out l, out r); Assert(l >= 0 && l <= 1 && r >= 0 && r <= 1, "Requested score stays in range"); }
            }
            Console.WriteLine("PASS: " + checks + " song import assertions; MIDI, tempo map, MP3 decoding, pitch estimates, cache and invalid files. No audio or motors played.");
            return 0;
        }
        catch (Exception problem) { Console.Error.WriteLine(problem); return 1; }
    }
}
