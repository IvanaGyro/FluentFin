using Flyleaf.FFmpeg;
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
[Collection(AudioDevice.Name)]
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

	/// <summary>
	/// Flyleaf.FFmpeg.ABI mirrors the structs of FFmpeg 9.0, and Flyleaf's audio decoder writes min_samples/max_samples
	/// into avfilter's internal FilterLink, which starts with the public AVFilterLink. An FFmpeg whose AVFilterLink is
	/// laid out differently (master builds after 9.0 added chroma_location) gets those writes in the wrong fields and
	/// crashes on the first audio frame (issue 23, arm64 builds with FFmpeg master). This checks the layout of the
	/// FFmpeg the app ships against the ABI, on any CPU.
	/// </summary>
	[Fact]
	public unsafe void FFmpeg_HasTheFilterLinkLayoutFlyleafWritesTo()
	{
		StartEngine();

		var graph = Raw.avfilter_graph_alloc();
		try
		{
			AVFilterContext* source = null;
			AVFilterContext* sink = null;
			Assert.Equal(0, Raw.avfilter_graph_create_filter(ref source, Raw.avfilter_get_by_name("abuffer"), "in", "channel_layout=stereo:sample_fmt=flt:sample_rate=48000:time_base=1/48000", null, graph));
			Assert.Equal(0, Raw.avfilter_graph_create_filter(ref sink, Raw.avfilter_get_by_name("abuffersink"), "out", null, null, graph));
			Assert.Equal(0, Raw.avfilter_link(source, 0, sink, 0));
			Assert.Equal(0, Raw.avfilter_graph_config(graph, null));

			var link = (FilterLink*)sink->inputs[0];
			Assert.True(link->pub.dst == sink, "AVFilterLink.dst is not the sink: the public AVFilterLink differs from the ABI");
			Assert.True(link->graph == graph, $"FilterLink.graph is not the graph: the loaded avfilter {Raw.avfilter_version() >> 16}.{(Raw.avfilter_version() >> 8) & 255} lays out AVFilterLink differently from the ABI Flyleaf is built for (FFmpeg 9.0), and Flyleaf's writes to FilterLink corrupt it");
		}
		finally
		{
			Raw.avfilter_graph_free(ref graph);
		}
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

	/// <summary>
	/// What the app does when it resumes an item: open with autoplay, seek to the resume position at once (while Flyleaf
	/// still sets up the audio filters and the audio device) and, for a track picked on the item's page, switch the audio
	/// track a moment later. Repeated, as the crash on arm64 (an access violation in av_buffersink_get_frame_flags) depends
	/// on timing.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Audio_ReachesTheAudioDevice_WhenSeekingRightAfterOpening(bool switchTrack)
	{
		StartEngine();
		Assert.False(Engine.Audio.Failed, "Flyleaf found no audio device. The workflows install a virtual one; locally, enable a playback device.");

		for (var i = 0; i < 8; i++)
		{
			using var player = new Player();
			var opened = player.Open(Media);
			Assert.True(opened.Success, opened.Error);
			player.SeekAccurate(6000);
			if (switchTrack)
			{
				Thread.Sleep(300);
				player.Open(player.Audio.Streams[i % 4]);
			}

			var seconds = PlayAndMeasure(player, TimeSpan.FromSeconds(2));

			Report(player, $"seek right after opening #{i}{(switchTrack ? $", switched to track {i % 4}" : "")}", seconds);
			AssertNoErrors(player);
			Assert.True(player.CurTime >= TimeSpan.FromSeconds(6.5).Ticks, $"the seek to 6s did not happen, the position is {TimeSpan.FromTicks(player.CurTime)}");
			Assert.True(seconds > 0.8, $"{seconds:0.00}s of audio reached the audio device in 2s of playback after the seek");
		}
	}

	/// <summary>
	/// Plays a file with every decoder, filter and renderer path Flyleaf sets up for it, also without an audio device;
	/// the audio is checked when there is one. A native crash in FFmpeg (e.g. an FFmpeg build that does not match the
	/// ABI Flyleaf was built for) kills the test host.
	/// </summary>
	[Theory]
	[InlineData("audio-tracks.mkv")]
	[InlineData("hevc-main10.mkv")]
	public void Playback_Advances(string file)
	{
		StartEngine();
		using var player = new Player();
		player.Config.Player.AutoPlay = false;
		var opened = player.Open(Path.Combine(AppContext.BaseDirectory, "Media", file));
		Assert.True(opened.Success, opened.Error);

		player.Play();
		Thread.Sleep(TimeSpan.FromSeconds(1));
		var seconds = PlayAndMeasure(player, TimeSpan.FromSeconds(2));

		Assert.False(string.IsNullOrWhiteSpace(PlayerLog(player)), "Flyleaf's log got no lines of the player");
		Report(player, $"{file} (video {player.decoder.VideoStream?.Codec} {player.decoder.VideoStream?.PixelFormatStr} accelerated={player.decoder.VideoDecoder.VideoAccelerated}, audio device found: {!Engine.Audio.Failed})", seconds);
		AssertNoErrors(player);
		Assert.True(player.CurTime >= TimeSpan.FromSeconds(2).Ticks, $"playback did not advance, the position is {TimeSpan.FromTicks(player.CurTime)}");
		Assert.True(Engine.Audio.Failed || seconds > 1, $"{seconds:0.00}s of audio reached the audio device in 2s of playback");
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

	/// <summary>
	/// Starts Flyleaf's engine (once per process) with the app's configuration and the app's FFmpeg folder, on a thread of its own that
	/// pumps window messages, as the UI thread of the app does.
	/// </summary>
	/// <remarks>
	/// Flyleaf takes the thread that starts the engine as its UI thread (the dispatcher of the WPF application it creates there) and invokes
	/// it, and waits, when a player is created on another thread. A test thread pumps no messages: the engine was started on the thread of
	/// the first test, only the players created on that same thread came back, and the first one created elsewhere hung the run.
	/// </remarks>
	internal static void StartEngine()
	{
		lock (EngineLock)
		{
			if (Engine.IsLoaded)
			{
				return;
			}

			var config = FlyleafEngineConfig.Create(FFmpegFolder());
			config.UIRefresh = false; // no UI to refresh in the tests
			// Flyleaf's log, to check the errors Flyleaf swallows; written line by line instead of in batches of 20
			config.LogOutput = LogFile;
			config.LogLevel = Enum.TryParse<FlyleafLib.LogLevel>(Environment.GetEnvironmentVariable("FLYLEAF_TEST_LOG_LEVEL"), out var level) ? level : FlyleafLib.LogLevel.Info;
			config.LogCachedLines = 0;

			Exception? failure = null;
			using var started = new ManualResetEventSlim();
			var thread = new Thread(() =>
			{
				try
				{
					Engine.Start(config);
				}
				catch (Exception ex)
				{
					failure = ex;
				}
				finally
				{
					started.Set();
				}

				while (NativeMessages.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
				{
					NativeMessages.TranslateMessage(ref message);
					NativeMessages.DispatchMessage(ref message);
				}
			})
			{
				IsBackground = true,
				Name = "Flyleaf UI",
			};
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			started.Wait();
			if (failure is not null)
			{
				throw new InvalidOperationException("Flyleaf's engine did not start", failure);
			}
		}
	}

	private static class NativeMessages
	{
		[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
		public struct Message
		{
			public IntPtr Window;
			public uint Id;
			public IntPtr WParam;
			public IntPtr LParam;
			public uint Time;
			public int X;
			public int Y;
			public uint Private;
		}

		[System.Runtime.InteropServices.DllImport("user32.dll")]
		public static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

		[System.Runtime.InteropServices.DllImport("user32.dll")]
		public static extern bool TranslateMessage(ref Message message);

		[System.Runtime.InteropServices.DllImport("user32.dll")]
		public static extern IntPtr DispatchMessage(ref Message message);
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
