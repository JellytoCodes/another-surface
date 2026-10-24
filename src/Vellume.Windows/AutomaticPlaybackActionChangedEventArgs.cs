using Vellume.Core;

namespace Vellume.Windows;

internal sealed class AutomaticPlaybackActionChangedEventArgs(
    ApplicationPlaybackAction action) : EventArgs
{
    public ApplicationPlaybackAction Action { get; } = action;
}
