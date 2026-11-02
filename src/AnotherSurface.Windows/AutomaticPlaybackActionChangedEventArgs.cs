using AnotherSurface.Core;

namespace AnotherSurface.Windows;

internal sealed class AutomaticPlaybackActionChangedEventArgs(
    ApplicationPlaybackAction action) : EventArgs
{
    public ApplicationPlaybackAction Action { get; } = action;
}
