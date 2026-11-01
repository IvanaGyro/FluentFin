using FlyleafLib;

namespace FluentFin.MediaPlayers;

public static class FlyleafEngineConfig
{
	/// <summary>
	/// The configuration Flyleaf's engine is started with. The tests start the engine with the same one.
	/// </summary>
	/// <remarks>
	/// The load profile must include avfilter and swscale: Flyleaf decodes every audio frame through an avfilter graph
	/// and converts video with swscale when the GPU can not. <see cref="Flyleaf.FFmpeg.LoadProfile.Main"/> loads neither,
	/// and Flyleaf then plays the video without any sound and without logging an error (issue 23).
	/// <see cref="Flyleaf.FFmpeg.LoadProfile.Filters"/> loads both and leaves out avdevice, which only capture devices need.
	/// </remarks>
	public static EngineConfig Create(string ffmpegPath, string? logOutput = null, LogLevel logLevel = LogLevel.Quiet) => new()
	{
		FFmpegLoadProfile = Flyleaf.FFmpeg.LoadProfile.Filters,
		FFmpegPath = ffmpegPath,
		FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Quiet,
		LogOutput = logOutput,
		LogLevel = logLevel,
		UIRefresh = true,    // Required for Activity, BufferedDuration, Stats in combination with Config.Player.Stats = true
		UIRefreshInterval = 250,      // How often (in ms) to notify the UI
	};
}
