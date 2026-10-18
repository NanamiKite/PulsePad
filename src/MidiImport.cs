using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XboxRumble
{
    internal static class MidiImport
    {
        private sealed class Note { public long Start, End; public int Pitch; }
        private sealed class Voice { public string Name; public int Track, Channel; public readonly List<Note> Notes = new List<Note>(); }
        private sealed class Tempo { public long Tick; public int Microseconds, Order; public double Seconds; }
        private sealed class Edge { public long Tick; public int Pitch, Delta; }
        private sealed class Reader
        {
            public readonly byte[] Bytes;
            public int Position, End;
            public Reader(byte[] bytes) { Bytes = bytes; End = bytes.Length; }
            public byte Byte() { if (Position >= End) throw new InvalidDataException("MIDI 文件被截断"); return Bytes[Position++]; }
            public int U16() { return Byte() * 256 + Byte(); }
            public uint U32() { return (uint)(U16() * 65536u + U16()); }
            public int Variable()
            {
                int result = 0;
                for (int i = 0; i < 4; i++) { byte b = Byte(); result = (result << 7) | (b & 127); if (b < 128) return result; }
                throw new InvalidDataException("MIDI 变长整数无效");
            }
            public string Tag() { return new string(new[] { (char)Byte(), (char)Byte(), (char)Byte(), (char)Byte() }); }
            public void Skip(int length) { if (length < 0 || length > End - Position) throw new InvalidDataException("MIDI 数据长度错误"); Position += length; }
        }
        public static Melody[] Load(string path)
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("MIDI 文件请控制在 4 MB 以内");
            Reader input = new Reader(File.ReadAllBytes(path));
            if (input.Tag() != "MThd") throw new InvalidDataException("不是标准 MIDI 文件");
            uint header = input.U32();
            if (header < 6 || header > input.End - input.Position) throw new InvalidDataException("MIDI 文件头无效");
            int format = input.U16(), tracks = input.U16(), division = input.U16(); input.Skip((int)header - 6);
            if (format > 1 || tracks < 1 || tracks > 128 || (format == 0 && tracks != 1)) throw new InvalidDataException("支持 MIDI 格式 0 / 1，最多 128 轨");
            if (division == 0 || (division & 0x8000) != 0) throw new InvalidDataException("暂不支持 SMPTE 时间制 MIDI，请导出为 PPQ 时间制");
            List<Voice> voices = new List<Voice>();
            List<Tempo> tempos = new List<Tempo>(); tempos.Add(new Tempo { Microseconds = 500000, Order = -1 });
            int eventCount = 0, noteOnCount = 0, melodicNoteOnCount = 0;
            for (int track = 0; track < tracks; track++)
            {
                if (input.Tag() != "MTrk") throw new InvalidDataException("缺少 MIDI 音轨");
                uint size = input.U32(); if (size > input.End - input.Position) throw new InvalidDataException("MIDI 音轨被截断");
                int fileEnd = input.End; input.End = input.Position + (int)size;
                int running = 0; long tick = 0; string trackName = "声部 " + (track + 1);
                Dictionary<int, Queue<long>> active = new Dictionary<int, Queue<long>>();
                Dictionary<int, Voice> channels = new Dictionary<int, Voice>();
                while (input.Position < input.End)
                {
                    if (++eventCount > 200000) throw new InvalidDataException("MIDI 事件过多");
                    tick += input.Variable();
                    if (tick / (double)division > 1000000) throw new InvalidDataException("MIDI 时长过长");
                    int status = input.Byte();
                    if (status < 128) { if (running == 0) throw new InvalidDataException("MIDI running status 无效"); input.Position--; status = running; }
                    else if (status < 240) running = status;
                    if (status == 255)
                    {
                        running = 0; int type = input.Byte(), length = input.Variable(), start = input.Position;
                        input.Skip(length);
                        if (type == 3 && length > 0) trackName = Encoding.UTF8.GetString(input.Bytes, start, Math.Min(length, 128)).Replace('\0', ' ').Trim();
                        if (type == 81 && length == 3)
                        {
                            int value = (input.Bytes[start] << 16) | (input.Bytes[start + 1] << 8) | input.Bytes[start + 2];
                            if (value == 0) throw new InvalidDataException("MIDI 速度不能为零");
                            tempos.Add(new Tempo { Tick = tick, Microseconds = value, Order = eventCount });
                        }
                        if (type == 47) { input.Position = input.End; break; }
                        continue;
                    }
                    if (status == 240 || status == 247) { running = 0; input.Skip(input.Variable()); continue; }
                    if (status >= 240) throw new InvalidDataException("不支持的 MIDI 系统事件");
                    int command = status >> 4, channel = status & 15, a = input.Byte(), b = command == 12 || command == 13 ? 0 : input.Byte();
                    if (a > 127 || b > 127) throw new InvalidDataException("MIDI 数据字节无效");
                    if (command == 9 && b > 0) { noteOnCount++; if (channel != 9) melodicNoteOnCount++; }
                    if (channel == 9 || (command != 8 && command != 9)) continue;
                    Voice voice;
                    if (!channels.TryGetValue(channel, out voice)) { voice = new Voice { Track = track, Channel = channel }; channels.Add(channel, voice); }
                    int key = channel * 128 + a; Queue<long> pending;
                    if (!active.TryGetValue(key, out pending)) { pending = new Queue<long>(); active.Add(key, pending); }
                    if (command == 9 && b > 0) pending.Enqueue(tick);
                    else if (pending.Count > 0)
                    {
                        long start = pending.Dequeue();
                        if (tick > start) voice.Notes.Add(new Note { Start = start, End = tick, Pitch = a });
                    }
                }
                foreach (KeyValuePair<int, Queue<long>> pair in active)
                    while (pair.Value.Count > 0)
                    {
                        long start = pair.Value.Dequeue();
                        if (tick > start) channels[pair.Key / 128].Notes.Add(new Note { Start = start, End = tick, Pitch = pair.Key % 128 });
                    }
                foreach (Voice voice in channels.Values)
                {
                    voice.Name = trackName + (channels.Count > 1 ? " / 通道 " + (voice.Channel + 1) : "");
                    if (voice.Notes.Count > 0) voices.Add(voice);
                }
                input.End = fileEnd;
            }
            if (voices.Count == 0)
            {
                if (noteOnCount == 0) throw new InvalidDataException("文件中没有音符。请从原谱重新导出 MIDI，并包含需要播放的乐器音轨。");
                if (melodicNoteOnCount == 0) throw new InvalidDataException("文件中只有鼓组音符。请导出含有旋律乐器音轨的 MIDI。");
                throw new InvalidDataException("文件中的音符没有有效时长。请检查原谱并重新导出 MIDI。");
            }
            tempos.Sort(delegate(Tempo a, Tempo b) { int order = a.Tick.CompareTo(b.Tick); return order != 0 ? order : a.Order.CompareTo(b.Order); });
            for (int i = 1; i < tempos.Count; i++) tempos[i].Seconds = tempos[i - 1].Seconds + (tempos[i].Tick - tempos[i - 1].Tick) * tempos[i - 1].Microseconds / (1000000.0 * division);
            voices.Sort(delegate(Voice a, Voice b) { return Score(b).CompareTo(Score(a)); });
            double bpm = Math.Max(1, Math.Min(300, 60000000.0 / tempos[tempos.FindLastIndex(delegate(Tempo t) { return t.Tick == 0; })].Microseconds));
            List<Melody> melodies = new List<Melody>();
            foreach (Voice voice in voices)
            {
                List<Edge> edges = new List<Edge>();
                foreach (Note note in voice.Notes) { edges.Add(new Edge { Tick = note.Start, Pitch = note.Pitch, Delta = 1 }); edges.Add(new Edge { Tick = note.End, Pitch = note.Pitch, Delta = -1 }); }
                edges.Sort(delegate(Edge a, Edge b) { return a.Tick.CompareTo(b.Tick); });
                List<int> notes = new List<int>(); List<double> lengths = new List<double>(); int[] counts = new int[128];
                long previous = 0; int current = 0;
                for (int i = 0; i < edges.Count;)
                {
                    long tick = edges[i].Tick;
                    if (tick > previous) { notes.Add(current); lengths.Add((Seconds(tick, tempos, division) - Seconds(previous, tempos, division)) * bpm / 60); }
                    while (i < edges.Count && edges[i].Tick == tick) { counts[edges[i].Pitch] += edges[i].Delta; i++; }
                    current = 127; while (current > 0 && counts[current] <= 0) current--;
                    previous = tick;
                }
                notes.Add(0); lengths.Add(1);
                string title = Path.GetFileNameWithoutExtension(path) + (voices.Count > 1 ? " · 声部 " + (melodies.Count + 1) : "");
                melodies.Add(new Melody(title, notes.ToArray(), lengths.ToArray(), bpm, "MIDI · " + voice.Name + " · " + voice.Notes.Count + " 音符", 0.9));
            }
            return melodies.ToArray();
        }
        private static double Score(Voice voice)
        {
            string name = voice.Name.ToLowerInvariant();
            double score = name.Contains("melod") || name.Contains("lead") || name.Contains("vocal") || name.Contains("主旋律") ? 1000 : 0;
            double sum = 0; foreach (Note note in voice.Notes) sum += note.Pitch;
            return score + sum / voice.Notes.Count + Math.Min(voice.Notes.Count, 100) * 0.1;
        }
        private static double Seconds(long tick, List<Tempo> tempos, int division)
        {
            int low = 0, high = tempos.Count;
            while (low < high) { int mid = (low + high) / 2; if (tempos[mid].Tick <= tick) low = mid + 1; else high = mid; }
            Tempo tempo = tempos[low - 1]; return tempo.Seconds + (tick - tempo.Tick) * tempo.Microseconds / (1000000.0 * division);
        }
    }
}
