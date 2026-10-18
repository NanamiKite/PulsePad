using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace XboxRumble
{
    internal static class AudioImport
    {
        internal const int Rate = 8000, MaximumSeconds = 600;
        internal static string FindDecoder()
        {
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string path = Path.Combine(entry.Trim().Trim('"'), "ffmpeg.exe"); if (Path.IsPathRooted(path) && File.Exists(path)) return path; }
                catch (ArgumentException) { }
            }
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg.exe");
            return File.Exists(bundled) ? bundled : null;
        }
        internal static float[] Decode(string path, CancellationToken cancellation)
        {
            string decoder = FindDecoder();
            if (decoder == null) throw new InvalidOperationException("MP3 / WAV 导入需要 FFmpeg。请将 ffmpeg.exe 加入 PATH，或放在程序的 tools 文件夹中。MIDI 导入不需要它。");
            path = Path.GetFullPath(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 100 * 1024 * 1024) throw new InvalidDataException("音频文件不存在或超过 100 MB");
            using (Process process = new Process())
            using (MemoryStream output = new MemoryStream())
            {
                process.StartInfo = new ProcessStartInfo(decoder, "-nostdin -hide_banner -loglevel error -protocol_whitelist file,pipe -i \"" + path + "\" -t 600.1 -vn -ac 1 -ar 8000 -f f32le pipe:1")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                StringBuilder errors = new StringBuilder();
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (errors) { if (errors.Length < 2000) errors.AppendLine(e.Data); } };
                process.Start(); process.BeginErrorReadLine();
                Action kill = delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } };
                bool timedOut = false;
                using (Timer timeout = new Timer(delegate { timedOut = true; kill(); }, null, 90000, Timeout.Infinite))
                using (cancellation.Register(kill))
                {
                    try
                    {
                        byte[] buffer = new byte[16384]; int count;
                        while ((count = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (output.Length + count > MaximumSeconds * Rate * 4) throw new InvalidDataException("音频最长支持 10 分钟，请先剪辑较长曲目");
                            output.Write(buffer, 0, count);
                        }
                        process.WaitForExit(); cancellation.ThrowIfCancellationRequested();
                        if (timedOut) throw new InvalidOperationException("音频解码超时");
                        if (process.ExitCode != 0) { lock (errors) throw new InvalidDataException("音频解码失败：" + errors.ToString().Trim()); }
                        if (output.Length == 0 || output.Length % 4 != 0) throw new InvalidDataException("没有有效音频采样");
                        float[] samples = new float[output.Length / 4]; Buffer.BlockCopy(output.GetBuffer(), 0, samples, 0, (int)output.Length);
                        return samples;
                    }
                    finally { kill(); }
                }
            }
        }
        internal static Melody Load(string path, CancellationToken cancellation)
        {
            return Analyze(Decode(path, cancellation), Path.GetFileNameWithoutExtension(path), cancellation);
        }
        internal static Melody Analyze(float[] samples, string name, CancellationToken cancellation)
        {
            if (samples.Length == 0 || samples.Length > Rate * MaximumSeconds) throw new InvalidDataException("音频长度无效");
            const int hop = 160, window = 1024, size = 2048;
            int[] pitches = new int[(samples.Length + hop - 1) / hop];
            double[] real = new double[size], imaginary = new double[size], energy = new double[window + 1], difference = new double[130];
            int voiced = 0;
            for (int frame = 0; frame < pitches.Length; frame++)
            {
                cancellation.ThrowIfCancellationRequested();
                Array.Clear(real, 0, size); Array.Clear(imaginary, 0, size);
                int start = frame * hop + hop / 2 - window / 2; double mean = 0;
                for (int i = 0; i < window; i++)
                {
                    int position = start + i;
                    double sample = position >= 0 && position < samples.Length ? samples[position] : 0;
                    if (double.IsNaN(sample) || double.IsInfinity(sample)) sample = 0;
                    real[i] = sample; mean += sample;
                }
                mean /= window;
                for (int i = 0; i < window; i++) { real[i] -= mean; energy[i + 1] = energy[i] + real[i] * real[i]; }
                if (energy[window] / window < 0.000036) continue;
                Fourier(real, imaginary, false);
                for (int i = 0; i < size; i++) { real[i] = real[i] * real[i] + imaginary[i] * imaginary[i]; imaginary[i] = 0; }
                Fourier(real, imaginary, true);
                // YIN-style normalized difference, obtained from autocorrelation in O(N log N).
                double running = 0;
                for (int lag = 1; lag < difference.Length; lag++)
                {
                    double d = Math.Max(0, energy[window - lag] + energy[window] - energy[lag] - 2 * real[lag]);
                    running += d; difference[lag] = running > 1e-12 ? d * lag / running : 1;
                }
                int best = 8;
                for (int lag = 8; lag <= 123; lag++)
                {
                    if (difference[lag] < difference[best]) best = lag;
                    if (difference[lag] < 0.18)
                    {
                        while (lag < 123 && difference[lag + 1] < difference[lag]) lag++;
                        best = lag; break;
                    }
                }
                if (difference[best] > 0.3) continue;
                double curvature = difference[best - 1] - 2 * difference[best] + difference[best + 1];
                double offset = Math.Abs(curvature) > 1e-10 ? 0.5 * (difference[best - 1] - difference[best + 1]) / curvature : 0;
                double hz = Rate / (best + Math.Max(-0.5, Math.Min(0.5, offset)));
                pitches[frame] = (int)Math.Round(69 + 12 * Math.Log(hz / 440, 2)); voiced++;
            }
            if (voiced == 0) throw new InvalidDataException("没有识别到稳定音高，建议使用旋律清晰的音频或 MIDI 文件");
            List<int> notes = new List<int>(); List<double> lengths = new List<double>();
            for (int i = 0; i < pitches.Length; i++)
            {
                int pitch = pitches[i];
                if (i > 0 && i + 1 < pitches.Length)
                {
                    int a = pitches[i - 1], b = pitches[i + 1];
                    pitch = Math.Max(Math.Min(a, pitch), Math.Min(Math.Max(a, pitch), b));
                }
                double beats = Math.Min(hop, samples.Length - i * hop) / (double)Rate * 2;
                if (notes.Count > 0 && notes[notes.Count - 1] == pitch) lengths[lengths.Count - 1] += beats;
                else { notes.Add(pitch); lengths.Add(beats); }
            }
            return new Melody(name, notes.ToArray(), lengths.ToArray(), 120, "音频", 1, true);
        }
        private static void Fourier(double[] real, double[] imaginary, bool inverse)
        {
            int n = real.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
                if (i >= j) continue;
                double value = real[i]; real[i] = real[j]; real[j] = value;
                value = imaginary[i]; imaginary[i] = imaginary[j]; imaginary[j] = value;
            }
            for (int width = 2; width <= n; width *= 2)
            {
                double angle = (inverse ? 2 : -2) * Math.PI / width, stepR = Math.Cos(angle), stepI = Math.Sin(angle);
                for (int start = 0; start < n; start += width)
                {
                    double wr = 1, wi = 0;
                    for (int j = 0; j < width / 2; j++)
                    {
                        int a = start + j, b = a + width / 2;
                        double r = wr * real[b] - wi * imaginary[b], im = wr * imaginary[b] + wi * real[b];
                        real[b] = real[a] - r; imaginary[b] = imaginary[a] - im; real[a] += r; imaginary[a] += im;
                        double next = wr * stepR - wi * stepI; wi = wr * stepI + wi * stepR; wr = next;
                    }
                }
            }
            if (inverse) for (int i = 0; i < n; i++) { real[i] /= n; imaginary[i] /= n; }
        }
    }
}
