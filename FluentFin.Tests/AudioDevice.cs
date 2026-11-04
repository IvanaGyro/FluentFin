namespace FluentFin.Tests;

/// <summary>
/// The tests that play audio share the one audio device of the runner and measure it, they must not run at the same time:
/// the sound of one is in the peak meter of the other, and the decoding of one slows down the other.
/// </summary>
public static class AudioDevice
{
	public const string Name = "Audio device";
}
