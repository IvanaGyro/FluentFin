using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.MediaPlayers;
using Jellyfin.Sdk.Generated.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xunit.Abstractions;

namespace FluentFin.Tests;

/// <summary>
/// The switch of the audio track in Windows Media Player, in the four ways it can go, with a server that takes its time:
/// <c>Media/switch/tracks.mkv</c> has two AAC tracks (A1, A2) and two DTS tracks (D1, D2). Whether a track is direct played or
/// transcoded is not decided here: the profile of the Windows media player (<see cref="WmpDeviceProfileManager.Build"/> with the
/// decoders of a machine) lists AAC and not DTS, and the server transcodes what the profile does not list (<c>hls-d0</c> and
/// <c>hls-d1</c> are the transcodes of D1 and D2, to AAC). Direct to direct is the player's own switch, as the view model decides it
/// (<see cref="AudioSwitchPolicy"/>); the others open another stream and resume at the position the item was at.
/// What is looked at: the position given to the time bar, every video frame the player produces and whether it would have been shown,
/// and the sound that reaches the audio device.
/// </summary>
[Collection(AudioDevice.Name)]
public sealed class WmpSwitchTests(ITestOutputHelper output)
{
	private static readonly TimeSpan ServerDelay = TimeSpan.FromMilliseconds(700);
	private static readonly TimeSpan ServerAnswer = TimeSpan.FromSeconds(2);

	private static readonly Guid[] InBoxAudio = [WmpSubtypes.Aac, WmpSubtypes.Mp3, WmpSubtypes.Mpeg, WmpSubtypes.Ac3, WmpSubtypes.Eac3, WmpSubtypes.Flac, WmpSubtypes.Alac, WmpSubtypes.WmaV9];

	/// <summary>The profile the server gets from a Windows media player with the decoders of a plain Windows (no DTS).</summary>
	private static readonly DeviceProfile Profile = WmpDeviceProfileManager.Build(new WmpCapabilities
	{
		VideoDecoders = new HashSet<Guid>([WmpSubtypes.H264, WmpSubtypes.Mpeg2, WmpSubtypes.Vc1]),
		AudioDecoders = new HashSet<Guid>(InBoxAudio),
		HardwareDecoders = new Dictionary<HardwareDecoder, VideoSize>(),
		IsHdrDisplay = false,
		CanPlayDolbyVision = false,
		AudioChannelCount = 2,
	});

	/// <summary>The streams of the file as the server lists them.</summary>
	private static readonly MediaSourceInfo Source = new()
	{
		Id = "switch",
		Container = "mkv",
		MediaStreams =
		[
			new MediaStream { Index = 0, Type = MediaStream_Type.Video, Codec = "h264" },
			new MediaStream { Index = 1, Type = MediaStream_Type.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
			new MediaStream { Index = 2, Type = MediaStream_Type.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
			new MediaStream { Index = 3, Type = MediaStream_Type.Audio, Codec = "dts", Channels = 2, SampleRate = 48000 },
			new MediaStream { Index = 4, Type = MediaStream_Type.Audio, Codec = "dts", Channels = 2, SampleRate = 48000 },
		],
	};

	/// <summary>An audio track of the file; <see cref="Direct"/> is what the profile says about its codec, as the server decides it.</summary>
	private sealed record Track(string Name, int StreamIndex, string? Hls)
	{
		public bool Direct => AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, StreamIndex, Profile);

		/// <summary>The position among the audio tracks of the file, which the player lists.</summary>
		public int Ordinal => StreamIndex - 1;
	}

	private static readonly Track A1 = new("A1", 1, null);
	private static readonly Track A2 = new("A2", 2, null);
	private static readonly Track D1 = new("D1", 3, "hls-d0/index.m3u8");
	private static readonly Track D2 = new("D2", 4, "hls-d1/index.m3u8");

	private static Track ByName(string name) => new[] { A1, A2, D1, D2 }.Single(x => x.Name == name);

	[Fact]
	public void TheProfileOfTheWindowsMediaPlayer_DirectPlaysAacAndTranscodesDts()
	{
		// the premise of the tests below: they would mean nothing if the profile listed DTS
		Assert.True(A1.Direct && A2.Direct, "AAC is not direct played by the profile");
		Assert.True(!D1.Direct && !D2.Direct, "DTS is direct played by the profile");
	}

	[Theory]
	[InlineData("A1", "A2")]
	[InlineData("A1", "D1")]
	[InlineData("D1", "A2")]
	[InlineData("D1", "D2")]
	public Task SwitchingTheTrack_NeverShowsTheStartOfTheMedia_AndKeepsThePosition(string fromName, string toName) => Switching(fromName, toName, TimeSpan.Zero);

	/// <summary>The server needs seconds for every segment of the transcode (7 s for the playlist and 13 s of buffering were seen on a 4K video).</summary>
	[Theory]
	[InlineData("A1", "D1")]
	[InlineData("D1", "D2")]
	public Task SwitchingToATranscode_WithASlowServer_NeverShowsTheStartOfTheMedia(string fromName, string toName) => Switching(fromName, toName, TimeSpan.FromSeconds(5));

	private async Task Switching(string fromName, string toName, TimeSpan segmentDelay)
	{
		var from = ByName(fromName);
		var to = ByName(toName);
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);
		using var session = new Session(server, output);

		await session.Open(from, TimeSpan.Zero);
		await Task.Delay(TimeSpan.FromSeconds(3.5));
		var at = session.Seeker.Position;
		server.SegmentDelay = segmentDelay;
		output.WriteLine($"{from.Name} -> {to.Name}: leaving {from.Name} at {at.TotalSeconds:0.0}s");

		session.StartRecording();
		await session.Switch(from, to, at);
		await Task.Delay(TimeSpan.FromSeconds(4));
		var peak = await AudioMeter.Peak(TimeSpan.FromSeconds(1.5));

		var name = $"{from.Name} -> {to.Name}{(segmentDelay > TimeSpan.Zero ? " (slow server)" : "")}";
		session.Report(name, at, peak);
		session.AssertGood(name, at, peak);
	}

	/// <summary>Leaving the item and playing it again, the first thing of the playback: resumed at the position it was left at.</summary>
	[Theory]
	[InlineData("A1")]
	[InlineData("D1")]
	public async Task PlayingAnItemAgain_NeverShowsTheStartOfTheMedia(string trackName)
	{
		var track = ByName(trackName);
		var resume = TimeSpan.FromSeconds(6);
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);
		using var session = new Session(server, output);

		session.StartRecording();
		await session.Open(track, resume);
		await Task.Delay(TimeSpan.FromSeconds(4));
		var peak = await AudioMeter.Peak(TimeSpan.FromSeconds(1.5));

		session.Report($"play again on {track.Name}", resume, peak);
		session.AssertGood($"play again on {track.Name}", resume, peak);
	}

	private sealed class Session : IDisposable
	{
		private readonly FakeMediaServer _server;
		private readonly ITestOutputHelper _output;
		private readonly MediaPlayer _player = new() { AutoPlay = false };
		private readonly List<TimeSpan> _timeBar = [];
		private readonly List<(bool Shown, TimeSpan Position)> _frames = [];
		private readonly IDisposable _timeBarSubscription;
		private MediaPlaybackItem? _item;
		private volatile bool _recording;

		public Session(FakeMediaServer server, ITestOutputHelper output)
		{
			_server = server;
			_output = output;
			Seeker = new WmpSeeker(_player);
			_timeBarSubscription = Seeker.PositionChanged.Subscribe(x =>
			{
				if (_recording)
				{
					lock (_timeBar) { _timeBar.Add(x); }
				}
			});

			// every video frame that is produced, and whether the app would show it
			_player.IsVideoFrameServerEnabled = true;
			_player.VideoFrameAvailable += (s, _) =>
			{
				if (_recording)
				{
					lock (_frames) { _frames.Add((Seeker.IsVideoVisible, s.PlaybackSession.Position)); }
				}
			};
			_player.MediaOpened += (_, _) => Opened?.Invoke();
		}

		public WmpSeeker Seeker { get; }

		private Action? Opened { get; set; }

		public void StartRecording() => _recording = true;

		/// <summary>What the controller does to play a stream and resume where the item was.</summary>
		public async Task Open(Track track, TimeSpan resume)
		{
			Seeker.BeginLoading();
			var uri = track.Direct ? _server.Url("tracks.mkv") : _server.Url(track.Hls!);
			var item = new MediaPlaybackItem(MediaSource.CreateFromUri(uri));
			_item = item;

			// a direct play has all the tracks: the item's one is selected once the media is open, after the position
			Opened = () => Seeker.OnMediaOpened(() =>
			{
				if (track.Direct)
				{
					item.AudioTracks.SelectedIndex = track.Ordinal;
				}
			});

			_player.Source = item;
			_player.Play();
			Seeker.PlayRequested = true;
			if (resume > TimeSpan.Zero)
			{
				Seeker.SeekTo(resume);
			}

			// loaded: where the item is going to play
			await WaitFor(() => Seeker.IsVideoVisible, TimeSpan.FromSeconds(60));
		}

		public async Task Switch(Track from, Track to, TimeSpan at)
		{
			if (from.Direct && to.Direct)
			{
				// the player's own switch, the file is the same
				_item!.AudioTracks.SelectedIndex = to.Ordinal;
				return;
			}

			// what the view model does: hold the position, stop the player, ask the server (takes its time), open the stream, resume
			Seeker.Hold(at);
			_player.Pause();
			_player.Source = null;
			await Task.Delay(ServerAnswer);
			await Open(to, at);
		}

		public void Report(string name, TimeSpan resumedAt, float peak)
		{
			string bar, frames;
			lock (_timeBar) { bar = string.Join(", ", _timeBar.Select(x => x.TotalSeconds.ToString("0.0"))); }
			lock (_frames) { frames = string.Join(", ", _frames.Select(x => $"{(x.Shown ? "" : "(hidden)")}{x.Position.TotalSeconds:0.0}")); }
			_output.WriteLine($"{name}: resumed at {resumedAt.TotalSeconds:0.0}s, peak={peak:0.000}, state={_player.PlaybackSession.PlaybackState}, position={_player.PlaybackSession.Position:g}, video visible={Seeker.IsVideoVisible}");
			_output.WriteLine($"{name}: time bar: {bar}");
			_output.WriteLine($"{name}: video frames (positions): {frames}");
		}

		public void AssertGood(string name, TimeSpan resumedAt, float peak)
		{
			var early = resumedAt - TimeSpan.FromSeconds(2);

			lock (_timeBar)
			{
				Assert.True(_timeBar.All(x => x >= early), $"{name}: the time bar was given {string.Join(", ", _timeBar.Where(x => x < early))}");
			}

			lock (_frames)
			{
				// a frame of the start of the media is produced while the player gets there; it must not be shown
				var shownEarly = _frames.Where(x => x.Shown && x.Position < early).ToList();
				Assert.True(shownEarly.Count == 0, $"{name}: the video showed {shownEarly.Count} frames of the start of the media: {string.Join(", ", shownEarly.Select(x => x.Position.TotalSeconds.ToString("0.0")))}");
			}

			Assert.True(Seeker.IsVideoVisible, $"{name}: the video was still hidden at the end");
			Assert.Equal(MediaPlaybackState.Playing, _player.PlaybackSession.PlaybackState);
			Assert.True(_player.PlaybackSession.Position >= resumedAt, $"{name}: the player is at {_player.PlaybackSession.Position}, it resumed at {resumedAt}");
			Assert.True(peak > 0.01f, $"{name}: no sound reached the audio device (peak {peak:0.000})");
		}

		private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
		{
			var end = DateTime.UtcNow + timeout;
			while (DateTime.UtcNow < end && !condition())
			{
				await Task.Delay(50);
			}
		}

		public void Dispose()
		{
			_timeBarSubscription.Dispose();
			_player.Dispose();
		}
	}
}
