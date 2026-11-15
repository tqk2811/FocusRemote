namespace FocusRemote.Protocol.Messages.Models.Control;

/// <summary>One capturable source. <see cref="Id"/> = <c>w:&lt;hwnd decimal&gt;</c> or <c>m:&lt;index&gt;</c>; <see cref="Kind"/> = <c>window</c>/<c>monitor</c>.</summary>
public sealed class SourceInfo
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}
