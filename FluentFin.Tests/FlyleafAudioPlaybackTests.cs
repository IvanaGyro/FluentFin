using FluentFin.MediaPlayers;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Xunit.Abstractions;

namespace FluentFin.Tests;

/// <summary>
/// Issue #23: Flyleaf played no sound for any audio codec. The app loaded FFmpeg without avfilter, Flyleaf decodes
/// every audio frame through an avfilter graph, and the failure was swallowed inside Flyleaf's audio decoder.
/// These tests start Flyleaf the way the app does and play <c>Media/audio-tracks.mkv</c> (H.264 video, E-AC-3 5.1,
/// AC-3 5.1, AAC 5.1 and E-AC-3 stereo audio). The playback tests need an audio device; the workflows install a
/// virtual one on the runner.
/// </summary>
public sealed class FlyleafAudioPlaybackTests(ITestOutputHelper output)
{
	private static readonly object EngineLock = new();
	private static readonly string LogFile = Path.Combine(Path.GetTempPath(), $"fluentfin-flyleaf-test-{Environment.ProcessId}.log");
	private static readonly string Media = Path.Combine(AppContext.BaseDirectory, "Media", "audio-tracks.mkv");

	public static TheoryData<int, string, int> Tracks => new()
	{
		{ 0, "eac3", 6 },
		{ 1, "ac3", 6 },
		{ 2, "aac", 6 },
		{ 3, "eac3", 2 },
	};

	[Fact]
	public void Engine_LoadsTheFFmpegLibrariesFlyleafDecodesWith()
	{
		StartEngine();

		// Flyleaf's audio decoder needs avfilter (abuffer -> abuffersink) and its video renderer falls back to swscale
		var avfilter = Record.Exception(() => { Flyleaf.FFmpeg.Raw.avfilter_version(); });
		var swscale = Record.Exception(() => { Flyleaf.FFmpeg.Raw.swscale_version(); });

		Assert.True(avfilter is null, $"avfilter is not loaded, Flyleaf decodes no audio: {avfilter?.Message}");
		Assert.True(swscale is null, $"swscale is not loaded: {swscale?.Message}");
	}

	[Theory]
	[MemberData(nameof(Tracks))]
	public void Audio_ReachesTheAudioDevice(int track, string codec, int channels)
	{
		using var player = Open(track);
		var stream = player.Audio.Streams[track];
		Assert.Equal(codec, stream.Codec);
		Assert.Equal(channels, stream.Channels);

		player.Play();
		var seconds = PlayAndMeasure(player, TimeSpan.FromSeconds(3));

		Report(player, $"{codec} {channels}ch", seconds);
		AssertNoErrors(player);
		Assert.True(seconds > 1.5, $"{seconds:0.00}s of {codec} {channels}ch audio reached the audio device in 3s of playback");
	}

	/// <summary>The app seeks to the resume position right after it starts playing.</summary>
	[Fact]
	public void Audio_ReachesTheAudioDevice_AfterSeekingWhilePlaying()
	{
		using var player = Open(0);
		player.Play();
		PlayAndMeasure(player, TimeSpan.FromSeconds(1.5));

		player.SeekAccurate(6000);
		var seconds = PlayAndMeasure(player, TimeSpan.FromSeconds(3));

		Report(player, "after seek", seconds);
		AssertNoErrors(player);
		Assert.True(player.CurTime >= TimeSpan.FromSeconds(7).Ticks, $"the seek to 6s did not happen, the position is {TimeSpan.FromTicks(player.CurTime)}");
		Assert.True(seconds > 1.5, $"{seconds:0.00}s of audio reached the audio device in 3s of playback after the seek");
		var resyncs = PlayerLog(player).Split('\n').Count(x => x.Contains("Resync filters"));
		Assert.True(resyncs < 20, $"Flyleaf resynced its audio filters {resyncs} times");
	}

	/// <summary>The app switches to the audio track chosen on the item's page once Flyleaf lists the tracks.</summary>
	[Fact]
	public void Audio_ReachesTheAudioDevice_AfterSwitchingTheTrackWhilePlaying()
	{
		using var player = Open(0);
		player.Play();
		PlayAndMeasure(player, TimeSpan.FromSeconds(1.5));

		player.Open(player.Audio.Streams[2]);
		var seconds = PlayAndMeasure(player, TimeSpan.FromSeconds(3));

		Report(player, "after switching to aac", seconds);
		AssertNoErrors(player);
		Assert.True(player.Audio.Streams[2].Enabled, "Flyleaf did not switch to the AAC track");
		Assert.True(seconds > 1.5, $"{seconds:0.00}s of audio reached the audio device in 3s of playback after switching the track");
	}

	/// <summary>Opens the media paused, with the given audio track (index into <see cref="Audio.Streams"/>).</summary>
	private Player Open(int track)
	{
		StartEngine();
		Assert.False(Engine.Audio.Failed, "Flyleaf found no audio device. The workflows install a virtual one; locally, enable a playback device.");

		var player = new Player();
		player.Config.Player.AutoPlay = false;
		var opened = player.Open(Media);
		Assert.True(opened.Success, opened.Error);
		Assert.Equal(4, player.Audio.Streams.Count);
		if (!player.Audio.Streams[track].Enabled)
		{
			player.Open(player.Audio.Streams[track]);
		}

		return player;
	}

	/// <summary>Plays for <paramref name="duration"/> and returns the seconds of audio Flyleaf handed to the audio device meanwhile.</summary>
	private static double PlayAndMeasure(Player player, TimeSpan duration)
	{
		long bytes = 0;
		void Count(object? sender, FlyleafLib.MediaFramework.MediaFrame.AudioFrame frame) => Interlocked.Add(ref bytes, frame.dataLen);

		player.Audio.SamplesAdded += Count;
		Thread.Sleep(duration);
		player.Audio.SamplesAdded -= Count;

		var decoder = player.decoder.AudioDecoder;
		return Interlocked.Read(ref bytes) / (double)(Math.Max(1, decoder.SampleBytes) * Math.Max(1, decoder.SampleRate));
	}

	private void Report(Player player, string what, double seconds)
	{
		var decoder = player.decoder.AudioDecoder;
		output.WriteLine($"{what}: {seconds:0.00}s of audio delivered, status={player.Status}, position={TimeSpan.FromTicks(player.CurTime)}, " +
			$"decoder={decoder.Status} {decoder.SampleRate}Hz {decoder.SampleBytes}B/sample, device='{player.Audio.Device}', volume={player.Audio.Volume}, mute={player.Audio.Mute}");
		foreach (var line in PlayerLog(player).Split('\n').Where(x => x.Contains("| Error") || x.Contains("| Warn") || x.Contains("Audio initialized")).Take(10))
		{
			output.WriteLine("  flyleaf: " + line.TrimEnd());
		}
	}

	private static void AssertNoErrors(Player player)
	{
		var errors = PlayerLog(player).Split('\n').Where(x => x.Contains("| Error")).ToList();
		Assert.True(errors.Count == 0, "Flyleaf logged errors:\n" + string.Join('\n', errors.Take(5)));
	}

	/// <summary>Starts Flyleaf's engine (once per process) with the app's configuration and the app's FFmpeg folder.</summary>
	private static void StartEngine()
	{
		lock (EngineLock)
		{
			if (Engine.IsLoaded)
			{
				return;
			}

			var config = FlyleafEngineConfig.Create(FFmpegFolder(), LogFile, FlyleafLib.LogLevel.Info);
			config.UIRefresh = false; // no UI to refresh in the tests
			Engine.Start(config);
		}
	}

	/// <summary>The FFmpeg folder next to the app (copied with the app's output), as the app loads it.</summary>
	private static string FFmpegFolder()
	{
		var output = Path.Combine(AppContext.BaseDirectory, "FFMpeg");
		if (Directory.Exists(output))
		{
			return output;
		}

		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, "FluentFin", "FFMpeg");
			if (Directory.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new DirectoryNotFoundException("FluentFin/FFMpeg");
	}

	/// <summary>The lines of Flyleaf's log of one player (the file is shared by all players, and written in the background).</summary>
	private static string PlayerLog(Player player)
	{
		Thread.Sleep(300); // let Flyleaf write what it has queued
		if (!File.Exists(LogFile))
		{
			return "";
		}

		using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		var tag = $"[#{player.PlayerId}]";
		return string.Join('\n', reader.ReadToEnd().Split('\n').Where(x => x.Contains(tag)));
	}
}
