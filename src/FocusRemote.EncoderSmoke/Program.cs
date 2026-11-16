using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding;
using FocusRemote.Host.Encoding.Enums;
using FocusRemote.Host.Encoding.Models;

// Smoke test: capture the primary monitor with the encoder DLL's own capture (same D3D11 device),
// encode ~150 frames at 30 fps (the last frame is repeated when the screen is static) and write a raw Annex B H.264 file.
// Usage: FocusRemote.EncoderSmoke <out.h264> [frameCount] [encoderName] [width height]
// width/height (optional) set an encode size different from the capture (exercises VideoProcessor scaling).

if (args.Length < 1)
{
	Console.WriteLine("Usage: FocusRemote.EncoderSmoke <out.h264> [frameCount] [encoderName|-] [width height]");
	return 1;
}
string outputPath = args[0];
int frameCount = args.Length > 1 ? int.Parse(args[1]) : 150;
string? encoderName = args.Length > 2 && args[2] != "-" ? args[2] : null;
int? forcedWidth = args.Length > 4 ? int.Parse(args[3]) : null;
int? forcedHeight = args.Length > 4 ? int.Parse(args[4]) : null;
const int fps = 30;

using HardwareVideoEncoder encoder = new();
Console.WriteLine($"Adapter 0: {encoder.AdapterName} (vendor 0x{encoder.AdapterVendorId:X4})");

IntPtr monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
using ScreenCapture capture = new(encoder, CaptureTargetKind.Monitor, monitor);

// Wait for the first captured frame (Encode needs one).
Stopwatch warmUp = Stopwatch.StartNew();
while (!capture.Poll() && warmUp.ElapsedMilliseconds < 3000)
	Thread.Sleep(5);
if (!capture.HasFrame)
{
	Console.WriteLine("No captured frame within 3 s");
	return 2;
}
Console.WriteLine($"First captured frame after {warmUp.ElapsedMilliseconds} ms");

int width = (forcedWidth ?? capture.Size.Width) & ~1;
int height = (forcedHeight ?? capture.Size.Height) & ~1;
Console.WriteLine($"Capture size: {capture.Size.Width}x{capture.Size.Height} -> encode {width}x{height}");
encoder.Open(new VideoEncoderOptions
{
	Width = width,
	Height = height,
	Fps = fps,
	BitrateKbps = 8000,
	GopSeconds = 5,
	EncoderName = encoderName,
});
Console.WriteLine($"Encoder: {encoder.EncoderName}, zero-copy: {encoder.IsZeroCopy}");

using FileStream output = File.Create(outputPath);
long frameInterval = Stopwatch.Frequency / fps;
Stopwatch clock = Stopwatch.StartNew();
List<double> latencies = new(frameCount);
int packets = 0, keyframes = 0, newCaptureFrames = 0;
for (int i = 0; i < frameCount; i++)
{
	long due = i * frameInterval;
	while (clock.ElapsedTicks < due)
		Thread.Sleep(1);

	long start = Stopwatch.GetTimestamp();
	if (capture.Poll())
		newCaptureFrames++;
	long pollEnd = Stopwatch.GetTimestamp();

	long timestampUs = clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
	int bytes = 0;
	if (encoder.Encode(capture, forceKeyframe: i == 0 || i == frameCount / 2, timestampUs, out EncodedPacket packet))
	{
		output.Write(packet.Data);
		bytes = packet.Data.Length;
		packets++;
		if (packet.IsKeyframe)
			keyframes++;
	}
	long end = Stopwatch.GetTimestamp();
	double pollMs = (pollEnd - start) * 1000.0 / Stopwatch.Frequency;
	double totalMs = (end - start) * 1000.0 / Stopwatch.Frequency;
	latencies.Add(totalMs);
	Console.WriteLine($"frame {i,4}: poll {pollMs,6:F2} ms, poll+encode {totalMs,6:F2} ms, bytes {bytes}");
}

latencies.Sort();
Console.WriteLine($"Done: {packets} packets ({keyframes} keyframes), {newCaptureFrames} new capture frames, {output.Length} bytes");
Console.WriteLine($"Latency poll+encode ms: avg {latencies.Average():F2}, p50 {latencies[latencies.Count / 2]:F2}, p95 {latencies[(int)(latencies.Count * 0.95)]:F2}, max {latencies[^1]:F2}");
return 0;

static class NativeMethods
{
	internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

	[DllImport("user32.dll")]
	internal static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);
}
