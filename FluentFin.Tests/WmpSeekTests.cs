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
	/// What the app does to resume an item that has several audio tracks (a direct play): the controller opens the file, the seek waits
	/// for the media, and when it is open the audio track of the item is selected and the position is set. Both orders of the two:
	/// the position the player really plays from, and where it is after the seconds that follow.
	/// </summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Resuming_AnItemWithAudioTracks_PlaysFromTheResumePosition(bool selectTrackFirst)
	{
		var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Media", "audio-tracks.mkv"));
		using var player = new MediaPlayer { AutoPlay = false };
		var seeker = new WmpSeeker(player);
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
			if (selectTrackFirst)
			{
				item.AudioTracks.SelectedIndex = 2;
				seeker.NotifyOpened();
			}
			else
			{
				seeker.NotifyOpened();
				item.AudioTracks.SelectedIndex = 2;
			}
		};

		player.Source = item;
		player.Play();
		seeker.PlayRequested = true;
		seeker.SeekTo(Target);

		await Task.Delay(TimeSpan.FromSeconds(3));

		string text;
		lock (played) { text = string.Join(", ", played.Select(x => x.TotalSeconds.ToString("0.0"))); }
		output.WriteLine($"selectTrackFirst={selectTrackFirst}: state={player.PlaybackSession.PlaybackState}, position={player.PlaybackSession.Position:g}, selected={item.AudioTracks.SelectedIndex}, played: {text}");
		lock (played)
		{
			Assert.True(played.All(x => x >= Target - TimeSpan.FromSeconds(1)), $"selectTrackFirst={selectTrackFirst}: the player played from {string.Join(", ", played.Where(x => x < Target - TimeSpan.FromSeconds(1)))}");
		}

		Assert.True(player.PlaybackSession.Position >= Target - TimeSpan.FromSeconds(1), $"selectTrackFirst={selectTrackFirst}: the player is at {player.PlaybackSession.Position}, the resume position was {Target}");
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
