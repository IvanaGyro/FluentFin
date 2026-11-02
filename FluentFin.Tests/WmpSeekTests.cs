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
		using var subscription = seeker.PositionChanged.Subscribe(shown.Add);
		player.MediaOpened += (_, _) => seeker.NotifyOpened();

		player.Source = new MediaPlaybackItem(MediaSource.CreateFromUri(server.Url));
		player.Play();
		seeker.SeekTo(Target);

		await Task.Delay(TimeSpan.FromSeconds(4));

		output.WriteLine("shown: " + string.Join(", ", shown.Select(x => x.TotalSeconds.ToString("0.0"))));
		output.WriteLine($"state={player.PlaybackSession.PlaybackState}, actual={player.PlaybackSession.Position:g}, seeker={seeker.Position:g}");
		Assert.NotEmpty(shown);
		Assert.True(shown.All(x => x >= Target - TimeSpan.FromSeconds(2)), "the time bar was given " + string.Join(", ", shown.Where(x => x < Target - TimeSpan.FromSeconds(2))));
		Assert.True(seeker.Position >= Target - TimeSpan.FromSeconds(2), $"the player is at {seeker.Position}, the seek was to {Target}");
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
