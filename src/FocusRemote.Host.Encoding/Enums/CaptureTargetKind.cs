namespace FocusRemote.Host.Encoding.Enums;

/// <summary>What <see cref="ScreenCapture"/> captures. Values match <c>FrCaptureKind</c> in FocusRemoteEncoder.h.</summary>
public enum CaptureTargetKind
{
	/// <summary>Handle is an HWND.</summary>
	Window = 0,
	/// <summary>Handle is an HMONITOR.</summary>
	Monitor = 1,
}
