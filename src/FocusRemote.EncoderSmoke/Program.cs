using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusRemote.Host.Encoding;
using FocusRemote.Host.Encoding.Models;
using TqkLibrary.WindowCapture.Captures;

// Smoke test: capture the primary monitor with WinrtGraphicCapture, render into the encoder surface,
// encode ~150 frames at 30 fps and write a raw Annex B H.264 file.
// Usage: FocusRemote.EncoderSmoke <out.h264> [frameCount] [encoderName]

if (args.Length < 1)
{
	Console.WriteLine("Usage: FocusRemote.EncoderSmoke <out.h264> [frameCount] [encoderName]");
	return 1;
}
string outputPath = args[0];
int frameCount = args.Length > 1 ? int.Parse(args[1]) : 150;
string? encoderName = args.Length > 2 ? args[2] : null;
const int fps = 30;

using WinrtGraphicCapture capture = new() { MaxFps = fps };
IntPtr monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
if (!capture.InitMonitor(monitor))
{
	Console.WriteLine("InitMonitor failed");
	return 2;
}

Stopwatch waitSize = Stopwatch.StartNew();
while (capture.Size.IsEmpty && waitSize.ElapsedMilliseconds < 3000)
	Thread.Sleep(10);
int width = capture.Size.Width & ~1;
int height = capture.Size.Height & ~1;
Console.WriteLine($"Capture size: {capture.Size.Width}x{capture.Size.Height} -> encode {width}x{height}");

using HardwareVideoEncoder encoder = new();
Console.WriteLine($"Adapter 0: {encoder.AdapterName} (vendor 0x{encoder.AdapterVendorId:X4})");
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

// Wait for the first captured frame so frame 0 is not an empty (black) surface.
int renderedGeneration = encoder.SurfaceGeneration;
// Render(isNewSurface: true) returns true even without a captured frame (it draws an empty texture),
// so only a later Render(false) == true proves a real frame was drawn.
bool warmUpNewTarget = false;
capture.Render(encoder.InputSurface, true, ref warmUpNewTarget);
Stopwatch warmUp = Stopwatch.StartNew();
while (!capture.Render(encoder.InputSurface, false, ref warmUpNewTarget) && warmUp.ElapsedMilliseconds < 3000)
	Thread.Sleep(5);
Console.WriteLine($"First captured frame after {warmUp.ElapsedMilliseconds} ms");

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
	bool isNewSurface = renderedGeneration != encoder.SurfaceGeneration;
	bool isNewTargetView = false;
	if (capture.Render(encoder.InputSurface, isNewSurface, ref isNewTargetView))
		newCaptureFrames++;
	renderedGeneration = encoder.SurfaceGeneration;
	long renderEnd = Stopwatch.GetTimestamp();

	long timestampUs = clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
	if (encoder.Encode(forceKeyframe: i == 0 || i == frameCount / 2, timestampUs, out EncodedPacket packet))
	{
		output.Write(packet.Data);
		packets++;
		if (packet.IsKeyframe)
			keyframes++;
	}
	long end = Stopwatch.GetTimestamp();
	double renderMs = (renderEnd - start) * 1000.0 / Stopwatch.Frequency;
	double totalMs = (end - start) * 1000.0 / Stopwatch.Frequency;
	latencies.Add(totalMs);
	Console.WriteLine($"frame {i,4}: render {renderMs,6:F2} ms, render+encode {totalMs,6:F2} ms, bytes {(packets > 0 ? packet.Data.Length : 0)}");
}

latencies.Sort();
Console.WriteLine($"Done: {packets} packets ({keyframes} keyframes), {newCaptureFrames} new capture frames, {output.Length} bytes");
Console.WriteLine($"Latency render+encode ms: avg {latencies.Average():F2}, p50 {latencies[latencies.Count / 2]:F2}, p95 {latencies[(int)(latencies.Count * 0.95)]:F2}, max {latencies[^1]:F2}");
return 0;

static class NativeMethods
{
	internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

	[DllImport("user32.dll")]
	internal static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);
}
