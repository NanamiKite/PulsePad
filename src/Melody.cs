using System;
using System.IO;
using System.Text;

namespace XboxRumble
{
    public sealed class Melody
    {
        private readonly int[] notes;
        private readonly double[] lengths;
        private readonly double[] starts;
        private readonly double gate;
        private readonly int highest;
        public string Name { get; private set; }
        public string Detail { get; private set; }
        public double DefaultBpm { get; private set; }
        public bool IsAudio { get; private set; }
        public double Beats { get; private set; }
        internal Melody(string name, int[] notes, double[] lengths, double bpm = 90, string detail = "", double gate = 0.78, bool audio = false)
        {
            if (notes.Length == 0 || notes.Length > 100000 || notes.Length != lengths.Length) throw new ArgumentException("Invalid score");
            Name = name; this.notes = (int[])notes.Clone(); this.lengths = (double[])lengths.Clone();
            if (double.IsNaN(bpm) || double.IsInfinity(bpm) || bpm <= 0) throw new ArgumentException("Invalid tempo");
            DefaultBpm = Math.Max(1, Math.Min(300, bpm)); Detail = detail; IsAudio = audio; this.gate = gate;
            starts = new double[notes.Length];
            for (int i = 0; i < notes.Length; i++)
            {
                if (notes[i] < 0 || notes[i] > 127 || double.IsNaN(lengths[i]) || double.IsInfinity(lengths[i]) || lengths[i] <= 0) throw new ArgumentException("Invalid note");
                highest = Math.Max(highest, notes[i]); starts[i] = Beats; Beats += lengths[i];
            }
            if (Beats > 1000000) throw new ArgumentException("Score too long");
        }
        public void Sample(double beat, out double bass, out double voice)
        {
            bass = voice = 0;
            if (double.IsNaN(beat) || double.IsInfinity(beat) || beat < 0) beat = 0;
            beat %= Beats;
            int i = Array.BinarySearch(starts, beat);
            if (i < 0) i = ~i - 1;
            double position = beat - starts[i];
            // A short silence separates repeated notes and leaves the motor time to slow.
            if (notes[i] == 0 || position >= lengths[i] * gate) return;
            // This is an uncalibrated motor-speed ratio, NOT a frequency/PCM command.
            // ERM inertia, firmware curves and LRA devices do not reproduce exact pitches.
            voice = Math.Pow(2, (notes[i] - highest) / 12.0);
            double withinBeat = beat - Math.Floor(beat);
            bass = withinBeat < 0.22 ? (((int)Math.Floor(beat) % 4 == 0) ? 1 : 0.55) : 0;
        }
        // Audio analysis is cached as bounded note data so later launches do not decode again.
        internal void Save(string path)
        {
            using (BinaryWriter writer = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                writer.Write(0x31534D52); writer.Write(Name); writer.Write(Detail);
                writer.Write(DefaultBpm); writer.Write(IsAudio); writer.Write(gate); writer.Write(notes.Length);
                for (int i = 0; i < notes.Length; i++) { writer.Write((byte)notes[i]); writer.Write(lengths[i]); }
            }
        }
        internal static Melody Load(string path)
        {
            if (new FileInfo(path).Length > 2000000) throw new InvalidDataException("曲目缓存过大");
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (reader.ReadInt32() != 0x31534D52) throw new InvalidDataException("曲目缓存格式错误");
                string name = reader.ReadString(), detail = reader.ReadString();
                double bpm = reader.ReadDouble(); bool audio = reader.ReadBoolean(); double gate = reader.ReadDouble();
                int count = reader.ReadInt32();
                if (count < 1 || count > 100000 || double.IsNaN(bpm) || bpm < 1 || bpm > 300 || double.IsNaN(gate) || gate <= 0 || gate > 1) throw new InvalidDataException("曲目缓存无效");
                int[] notes = new int[count]; double[] lengths = new double[count];
                for (int i = 0; i < count; i++) { notes[i] = reader.ReadByte(); lengths[i] = reader.ReadDouble(); }
                return new Melody(name, notes, lengths, bpm, detail, gate, audio);
            }
        }
    }

    public static class MelodyLibrary
    {
        // Public-domain melodies; MIDI note numbers only describe the score, not measured motor pitch.
        public static readonly Melody[] All = {
            new Melody("小星星", new[] {
                60,60,67,67,69,69,67, 65,65,64,64,62,62,60,
                67,67,65,65,64,64,62, 67,67,65,65,64,64,62,
                60,60,67,67,69,69,67, 65,65,64,64,62,62,60, 0
            }, new double[] {
                1,1,1,1,1,1,2, 1,1,1,1,1,1,2,
                1,1,1,1,1,1,2, 1,1,1,1,1,1,2,
                1,1,1,1,1,1,2, 1,1,1,1,1,1,2, 2
            }),
            new Melody("欢乐颂", new[] {
                64,64,65,67,67,65,64,62,60,60,62,64,64,62,62,
                64,64,65,67,67,65,64,62,60,60,62,64,62,60,60, 0
            }, new double[] {
                1,1,1,1,1,1,1,1,1,1,1,1,1.5,0.5,2,
                1,1,1,1,1,1,1,1,1,1,1,1,1.5,0.5,2, 2
            }),
            new Melody("两只老虎", new[] {
                60,62,64,60,60,62,64,60,64,65,67,64,65,67,
                67,69,67,65,64,60,67,69,67,65,64,60,60,55,60,60,55,60, 0
            }, new double[] {
                1,1,1,1,1,1,1,1,1,1,2,1,1,2,
                0.5,0.5,0.5,0.5,1,1,0.5,0.5,0.5,0.5,1,1,1,1,2,1,1,2, 2
            })
        };
    }
}
