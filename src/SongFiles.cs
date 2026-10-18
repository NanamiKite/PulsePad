using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XboxRumble
{
    internal static class SongFiles
    {
        internal static Melody[] Import(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".mid" || extension == ".midi") return MidiImport.Load(path);
            if (extension == ".mp3" || extension == ".wav") return new[] { AudioImport.Load(path, cancellation) };
            throw new InvalidDataException("请选择 MIDI、MP3 或 WAV 文件");
        }
        internal static Melody[] LoadFolder(string directory, out string error)
        {
            List<Melody> songs = new List<Melody>(); error = "";
            if (!Directory.Exists(directory)) return songs.ToArray();
            try
            {
                string[] paths = Directory.GetFiles(directory); Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    try
                    {
                        string extension = Path.GetExtension(path).ToLowerInvariant();
                        if (extension == ".mid" || extension == ".midi") songs.AddRange(MidiImport.Load(path));
                        if (extension == ".rumble") songs.Add(Melody.Load(path));
                    }
                    catch (Exception problem) { error = Path.GetFileName(path) + "：" + problem.Message; }
                }
            }
            catch (IOException problem) { error = problem.Message; }
            catch (UnauthorizedAccessException problem) { error = problem.Message; }
            return songs.ToArray();
        }
        internal static void SaveImported(string directory, Melody[] songs)
        {
            Directory.CreateDirectory(directory);
            foreach (Melody song in songs)
            {
                string key;
                using (SHA256 hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(song.Name))).Replace("-", "").Substring(0, 20);
                string destination = Path.Combine(directory, key + ".rumble"), temporary = destination + ".tmp";
                try
                {
                    song.Save(temporary);
                    if (File.Exists(destination)) File.Replace(temporary, destination, null);
                    else File.Move(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
