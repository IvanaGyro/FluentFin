using System.Net;
using System.Net.Sockets;
using FluentFin.MediaPlayers;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xunit.Abstractions;

namespace FluentFin.Tests;

/// <summary>
/// After a switch to a transcoded audio track the app opens the stream and seeks to where the item was, right after it starts
/// playing. Windows Media Player cannot do that before the media is open, and the time bar went to zero for a few seconds. These
/// tests play an HLS stream (<c>Media/hls</c>, 12s, what the server sends for a transcode) from a local server and look at the
/// positions the time bar is given.
/// </summary>
[Collection(AudioDevice.Name)]
public sealed class WmpSeekTests(ITestOutputHelper output)
{
	private static readonly TimeSpan Target = TimeSpan.FromSeconds(8);

	[Fact]
	public async Task TheTimeBar_NeverShowsTheStartOfTheStream_WhenSeekingRightAfterPlay()
	{
		using var server = new LocalHls(Path.Combine(AppContext.BaseDirectory, "Media", "hls"));
		using var player = new MediaPlayer { AutoPlay = false };
		var seeker = new WmpSeeker(player);
		var shown = new List<TimeSpan>();
		var played = new List<TimeSpan>();
		using var subscription = seeker.PositionChanged.Subscribe(shown.Add);
		player.PlaybackSession.PositionChanged += (s, _) =>
		{
			// the position is zero while the media opens, what counts is what is played
			if (s.PlaybackState == MediaPlaybackState.Playing)
			{
				lock (played) { played.Add(s.Position); }
			}
		};
		player.MediaOpened += (_, _) => seeker.NotifyOpened();

		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url));
		player.Play();
		seeker.PlayRequested = true;
		seeker.SeekTo(Target);

		await Task.Delay(TimeSpan.FromSeconds(4));

		// what the player really played, not what the time bar was given: the video must not play from the start of the stream
		output.WriteLine("played: " + string.Join(", ", played.Select(x => x.TotalSeconds.ToString("0.0"))));
		lock (played)
		{
			Assert.NotEmpty(played);
			Assert.True(played.All(x => x >= Target - TimeSpan.FromSeconds(1)), "the player was at " + string.Join(", ", played.Where(x => x < Target - TimeSpan.FromSeconds(1))));
		}

		Assert.Equal(MediaPlaybackState.Playing, player.PlaybackSession.PlaybackState);

		output.WriteLine("shown: " + string.Join(", ", shown.Select(x => x.TotalSeconds.ToString("0.0"))));
		output.WriteLine($"state={player.PlaybackSession.PlaybackState}, actual={player.PlaybackSession.Position:g}, seeker={seeker.Position:g}");
		Assert.NotEmpty(shown);
		Assert.True(shown.All(x => x >= Target - TimeSpan.FromSeconds(2)), "the time bar was given " + string.Join(", ", shown.Where(x => x < Target - TimeSpan.FromSeconds(2))));
		Assert.True(seeker.Position >= Target - TimeSpan.FromSeconds(2), $"the player is at {seeker.Position}, the seek was to {Target}");
	}

	/// <summary>
	/// What the app does to resume an item that has several audio tracks (a direct play), with the order of the controller: the position
	/// the player really plays from, and where it is after the seconds that follow.
	/// </summary>
	[Fact]
	public async Task Resuming_AnItemWithAudioTracks_PlaysFromTheResumePosition()
	{
		var (played, position, selected) = await OpenAndResume(Target, trackFirst: false);

		output.WriteLine($"controller order: position={position:g}, selected={selected}, played: {string.Join(", ", played.Select(x => x.TotalSeconds.ToString("0.0")))}");
		Assert.True(played.All(x => x >= Target - TimeSpan.FromSeconds(1)), "the player played from " + string.Join(", ", played.Where(x => x < Target - TimeSpan.FromSeconds(1))));
		Assert.True(position >= Target - TimeSpan.FromSeconds(1), $"the player is at {position}, the resume position was {Target}");
		Assert.Equal(2, selected);
	}

	/// <summary>The order the controller had before: the track first. Reported, not asserted, it is what the platform does.</summary>
	[Fact]
	public async Task Baseline_ATrackSelectedBeforeThePosition_RestartsThePlayback()
	{
		var (played, position, _) = await OpenAndResume(Target, trackFirst: true);

		output.WriteLine($"track first: position={position:g}, played: {string.Join(", ", played.Select(x => x.TotalSeconds.ToString("0.0")))}");
	}

	/// <summary>
	/// The loop of the report: play an item, leave, play it again. What the app reports to the server when it leaves is the position of
	/// the player; the server keeps it and the next playback resumes there. Three playbacks in a row, each from where the last one was left.
	/// </summary>
	[Fact]
	public async Task TheLastPosition_IsRemembered_WhenTheItemIsPlayedAgain()
	{
		var server = TimeSpan.Zero;
		var positions = new List<TimeSpan>();

		for (var playback = 1; playback <= 3; playback++)
		{
			var (_, left, _) = await OpenAndResume(server, trackFirst: false, listen: TimeSpan.FromSeconds(2.5));
			positions.Add(left);

			// the report of the app when it leaves, the server keeps it
			Assert.True(left > server, $"playback {playback} started from {server} and was left at {left}");
			server = left;
		}

		output.WriteLine("left at: " + string.Join(", ", positions.Select(x => x.TotalSeconds.ToString("0.0"))));
		Assert.True(positions[2] > positions[1] && positions[1] > positions[0], "the positions did not go on: " + string.Join(", ", positions));
	}

	/// <summary>
	/// The same loop with the order the controller had (the track first), the build that was reported: every playback starts again from
	/// the start of the file, is left a few seconds later, and the server keeps that position. Reported, not asserted.
	/// </summary>
	[Fact]
	public async Task Baseline_TheLoopWithTheTrackSelectedFirst_StartsAgainFromTheStart()
	{
		var server = TimeSpan.FromSeconds(8);
		var positions = new List<string>();

		for (var playback = 1; playback <= 3; playback++)
		{
			var (played, left, _) = await OpenAndResume(server, trackFirst: true, listen: TimeSpan.FromSeconds(2.5));
			positions.Add($"started from {server.TotalSeconds:0.0}s, played {string.Join("/", played.Select(x => x.TotalSeconds.ToString("0.0")))}, left at {left.TotalSeconds:0.0}s");
			server = left;
		}

		output.WriteLine("track first: " + string.Join(" | ", positions));
	}

	/// <summary>Opens the file as the controller does and resumes at <paramref name="resumeAt"/>; returns what was played, where the player is when it is left, and the selected track.</summary>
	private async Task<(List<TimeSpan> Played, TimeSpan Position, int Selected)> OpenAndResume(TimeSpan resumeAt, bool trackFirst, TimeSpan? listen = null)
	{
		var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Media", "audio-tracks.mkv"));
		using var player = new MediaPlayer { AutoPlay = false };
		var seeker = new WmpSeeker(player);

		// the time bar listens to the position all the time, and the seek that is held ends when it sees the player at the position
		using var timeBar = seeker.PositionChanged.Subscribe(_ => { });
		var played = new List<TimeSpan>();
		player.PlaybackSession.PositionChanged += (s, _) =>
		{
			if (s.PlaybackState == MediaPlaybackState.Playing)
			{
				lock (played) { played.Add(s.Position); }
			}
		};

		var item = new MediaPlaybackItem(MediaSource.CreateFromUri(media));
		player.MediaOpened += (_, _) =>
		{
			if (trackFirst)
			{
				item.AudioTracks.SelectedIndex = 2;
				seeker.NotifyOpened();
			}
			else
			{
				seeker.OnMediaOpened(() => item.AudioTracks.SelectedIndex = 2);
			}
		};

		player.Source = item;
		player.Play();
		seeker.PlayRequested = true;
		if (resumeAt > TimeSpan.Zero)
		{
			seeker.SeekTo(resumeAt);
		}

		await Task.Delay(listen ?? TimeSpan.FromSeconds(3));

		// what the app reports when it leaves: the position of the player, while it plays
		var position = seeker.Position;
		lock (played) { return ([.. played], position, item.AudioTracks.SelectedIndex); }
	}

	/// <summary>
	/// The in-player switch of the audio track while the item plays (the menu, for a direct play): the position must go on, not go back.
	/// </summary>
	[Fact]
	public async Task SwitchingTheAudioTrack_WhilePlaying_KeepsThePosition()
	{
		var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Media", "audio-tracks.mkv"));
		using var player = new MediaPlayer { AutoPlay = false };
		var item = new MediaPlaybackItem(MediaSource.CreateFromUri(media));
		var opened = new TaskCompletionSource();
		player.MediaOpened += (_, _) => opened.TrySetResult();
		player.Source = item;
		player.Play();
		await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(15)));

		await Task.Delay(TimeSpan.FromSeconds(3));
		var before = player.PlaybackSession.Position;

		var after = new List<TimeSpan>();
		player.PlaybackSession.PositionChanged += (s, _) => { lock (after) { after.Add(s.Position); } };
		item.AudioTracks.SelectedIndex = 2;
		await Task.Delay(TimeSpan.FromSeconds(2));

		string text;
		lock (after) { text = string.Join(", ", after.Select(x => x.TotalSeconds.ToString("0.0"))); }
		output.WriteLine($"switch: before={before.TotalSeconds:0.0}, after: {text}");
		lock (after)
		{
			Assert.NotEmpty(after);
			Assert.True(after.All(x => x >= before - TimeSpan.FromSeconds(1)), $"the position was {before} before the switch, then {string.Join(", ", after.Where(x => x < before - TimeSpan.FromSeconds(1)))}");
		}
	}

	/// <summary>A pause of the user is kept when the seek has to wait for the media to open.</summary>
	[Fact]
	public async Task APausedPlayer_StaysPaused_WhenTheSeekWaitsForTheMediaToOpen()
	{
		using var server = new LocalHls(Path.Combine(AppContext.BaseDirectory, "Media", "hls"));
		using var player = new MediaPlayer { AutoPlay = false };
		var seeker = new WmpSeeker(player);
		player.MediaOpened += (_, _) => seeker.NotifyOpened();

		// the app asked for the pause after it asked to play, as an audio switch of a paused item does
		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url));
		player.Play();
		seeker.PlayRequested = true;
		seeker.SeekTo(Target);
		seeker.PlayRequested = false;
		seeker.CancelPlayWhenSeeked();
		player.Pause();

		await Task.Delay(TimeSpan.FromSeconds(4));

		output.WriteLine($"state={player.PlaybackSession.PlaybackState}, position={player.PlaybackSession.Position:g}");
		Assert.NotEqual(MediaPlaybackState.Playing, player.PlaybackSession.PlaybackState);
		Assert.True(player.PlaybackSession.Position >= Target - TimeSpan.FromSeconds(1), $"the player is at {player.PlaybackSession.Position}");
	}

	/// <summary>
	/// A seek while the open stream buffers (it played to the end of a segment and the server is slow to send the next one): the player
	/// cannot seek in that state, the seek is made all the same, and the time bar follows the player again once it plays. The media does
	/// not open a second time.
	/// </summary>
	[Fact]
	public async Task ASeekWhileTheOpenStreamBuffers_LetsTheTimeBarFollowThePlayerAgain()
	{
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "hls"), TimeSpan.Zero)
		{
			FileDelay = path => path.EndsWith("seg1.ts", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(6) : TimeSpan.Zero,
		};
		using var player = new MediaPlayer { AutoPlay = false };
		var seeker = new WmpSeeker(player);
		var shown = new List<TimeSpan>();
		using var subscription = seeker.PositionChanged.Subscribe(x => { lock (shown) { shown.Add(x); } });
		var states = new List<string>();
		player.PlaybackSession.PlaybackStateChanged += (s, _) => { lock (states) { states.Add($"{s.PlaybackState}@{s.Position.TotalSeconds:0.0}"); } };
		player.MediaOpened += (_, _) => seeker.NotifyOpened();

		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url("index.m3u8")));
		seeker.PlayRequested = true;
		player.Play();
		await WaitFor(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && player.PlaybackSession.Position >= TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));

		// the first segment plays to its end (4 s) and the player waits for the second one, which the server sends 6 s after it was asked
		await WaitFor(() => player.PlaybackSession.PlaybackState is not (MediaPlaybackState.Playing or MediaPlaybackState.Paused), TimeSpan.FromSeconds(10));
		var stateAtTheSecondSeek = player.PlaybackSession.PlaybackState;
		var second = TimeSpan.FromSeconds(5);
		seeker.SeekTo(second);

		await WaitFor(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && player.PlaybackSession.Position >= second + TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(20));
		await Task.Delay(TimeSpan.FromSeconds(0.5));
		var actual = player.PlaybackSession.Position;
		var reported = seeker.Position;
		TimeSpan last;
		lock (shown) { last = shown.LastOrDefault(); }
		string history;
		lock (states) { history = string.Join(", ", states); }

		output.WriteLine($"state at the seek={stateAtTheSecondSeek}, now={player.PlaybackSession.PlaybackState}, actual={actual:g}, seeker={reported:g}, last given to the time bar={last:g}");
		output.WriteLine("states: " + history);
		Assert.True(stateAtTheSecondSeek is not (MediaPlaybackState.Playing or MediaPlaybackState.Paused), $"the player could seek at the seek ({stateAtTheSecondSeek}), the test did not reach the case");
		Assert.True(actual >= second + TimeSpan.FromSeconds(1.5), $"the player is at {actual}, it did not play on after the seek to {second}");
		Assert.True((reported - actual).Duration() <= TimeSpan.FromSeconds(1), $"the time bar shows {reported}, the player is at {actual}");
		Assert.True((last - actual).Duration() <= TimeSpan.FromSeconds(1), $"the time bar was last given {last}, the player is at {actual}");
	}

	private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
	{
		var end = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < end && !condition())
		{
			await Task.Delay(50);
		}
	}

	/// <summary>What the controller did before: the position is set when asked, whatever the player can do. Reported, not asserted.</summary>
	[Fact]
	public async Task Baseline_ASeekRightAfterPlay_IsReported()
	{
		using var server = new LocalHls(Path.Combine(AppContext.BaseDirectory, "Media", "hls"));
		using var player = new MediaPlayer { AutoPlay = false };
		var shown = new List<TimeSpan>();
		player.PlaybackSession.PositionChanged += (s, _) => shown.Add(s.Position);

		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url));
		player.Play();
		try
		{
			player.PlaybackSession.Position = Target;
		}
		catch (Exception ex)
		{
			output.WriteLine("setting the position: " + ex.GetType().Name + " " + ex.Message);
		}

		await Task.Delay(TimeSpan.FromSeconds(4));

		output.WriteLine("baseline, the time bar was given: " + string.Join(", ", shown.Select(x => x.TotalSeconds.ToString("0.0"))));
		output.WriteLine($"baseline: state={player.PlaybackSession.PlaybackState}, actual={player.PlaybackSession.Position:g}");
	}

	/// <summary>
	/// The switch to a transcoded audio track: the app stops the player (it reports zero), asks the server, which takes seconds,
	/// and only then opens the stream and seeks. The time bar has to stay at the resume position through all of it.
	/// </summary>
	[Fact]
	public async Task TheTimeBar_StaysAtTheResumePosition_WhileThePlayerIsStoppedAndTheServerAnswers()
	{
		var shown = await Switch(hold: true);

		Assert.NotEmpty(shown);
		Assert.True(shown.All(x => x >= Target - TimeSpan.FromSeconds(2)), "the time bar was given " + string.Join(", ", shown.Where(x => x < Target - TimeSpan.FromSeconds(2))));
	}

	/// <summary>The same without holding the position, what the app did: reported, not asserted.</summary>
	[Fact]
	public async Task Baseline_TheTimeBarWhileThePlayerIsStopped_IsReported()
	{
		var shown = await Switch(hold: false);

		output.WriteLine("baseline, the time bar was given: " + string.Join(", ", shown.Select(x => x.TotalSeconds.ToString("0.0"))));
	}

	private async Task<List<TimeSpan>> Switch(bool hold)
	{
		using var server = new LocalHls(Path.Combine(AppContext.BaseDirectory, "Media", "hls"));
		using var player = new MediaPlayer { AutoPlay = false };
		var seeker = new WmpSeeker(player);
		var shown = new List<TimeSpan>();
		using var subscription = seeker.PositionChanged.Subscribe(x => { lock (shown) { shown.Add(x); } });
		player.MediaOpened += (_, _) => seeker.NotifyOpened();

		// an item that plays
		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url));
		player.Play();
		await Task.Delay(TimeSpan.FromSeconds(2.5));
		lock (shown) { shown.Clear(); }

		// the switch
		if (hold)
		{
			seeker.Hold(Target);
		}

		player.Pause();
		player.Source = null;
		await Task.Delay(TimeSpan.FromSeconds(2));

		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url));
		player.Play();
		seeker.PlayRequested = true;
		seeker.SeekTo(Target);
		await Task.Delay(TimeSpan.FromSeconds(4));

		output.WriteLine($"hold={hold}: state={player.PlaybackSession.PlaybackState}, actual={player.PlaybackSession.Position:g}, seeker={seeker.Position:g}");
		lock (shown) { return [.. shown]; }
	}

	/// <summary>The files of a folder over http on localhost, the way the server sends an HLS transcode.</summary>
	private sealed class LocalHls : IDisposable
	{
		private readonly HttpListener _listener = new();
		private readonly string _folder;

		public LocalHls(string folder)
		{
			_folder = folder;
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();

			_listener.Prefixes.Add($"http://localhost:{port}/");
			_listener.Start();
			Url = new Uri($"http://localhost:{port}/index.m3u8");
			_ = Task.Run(Serve);
		}

		public Uri Url { get; }

		private async Task Serve()
		{
			while (_listener.IsListening)
			{
				HttpListenerContext context;
				try
				{
					context = await _listener.GetContextAsync();
				}
				catch
				{
					return;
				}

				try
				{
					var path = Path.Combine(_folder, Path.GetFileName(context.Request.Url!.AbsolutePath));
					if (!File.Exists(path))
					{
						context.Response.StatusCode = 404;
					}
					else
					{
						var bytes = await File.ReadAllBytesAsync(path);
						context.Response.ContentType = path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
						context.Response.ContentLength64 = bytes.Length;
						await context.Response.OutputStream.WriteAsync(bytes);
					}
				}
				catch
				{
				}
				finally
				{
					context.Response.Close();
				}
			}
		}

		public void Dispose() => _listener.Close();
	}
}
