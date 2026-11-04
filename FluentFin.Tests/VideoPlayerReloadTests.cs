using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.MediaPlayers;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Tests;

/// <summary>
/// The view model with a real player of each kind and a local server: what happens when an item is loaded again, after another one
/// ended, or after a stream failed to open, and the seeks that the players hold until they can make them.
/// </summary>
[Collection(AudioDevice.Name)]
public sealed class VideoPlayerReloadTests(ITestOutputHelper output)
{
	private static readonly TimeSpan ServerDelay = TimeSpan.FromMilliseconds(50);

	static VideoPlayerReloadTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public static TheoryData<MediaPlayerType> AllPlayers => new()
	{
		MediaPlayerType.WindowsMediaPlayer,
		MediaPlayerType.Vlc,
		MediaPlayerType.Flyleaf,
	};

	/// <summary>The streams of <c>Media/switch/tracks.mkv</c> as the server lists them.</summary>
	private static MediaSourceInfo Source() => new()
	{
		Id = "switch",
		Container = "mkv",
		DefaultAudioStreamIndex = 3,
		MediaStreams =
		[
			new MediaStream { Index = 0, Type = MediaStream_Type.Video, Codec = "h264" },
			new MediaStream { Index = 1, Type = MediaStream_Type.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
			new MediaStream { Index = 2, Type = MediaStream_Type.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
			new MediaStream { Index = 3, Type = MediaStream_Type.Audio, Codec = "dts", Channels = 2, SampleRate = 48000 },
			new MediaStream { Index = 4, Type = MediaStream_Type.Audio, Codec = "dts", Channels = 2, SampleRate = 48000 },
		],
	};

	// The players raise their events on their own threads (VLC on its event thread, which must not call VLC back): what waits for an event
	// goes on on another thread, as the app goes on on the UI thread.

	/// <summary>
	/// The episode ends and the next one of the playlist is selected (what the handler of <c>Ended</c> and the handler of the selection
	/// do): the next one plays by itself. The player has stopped playing at the end, and that must not be taken for a pause of the user.
	/// </summary>
	[Theory(Timeout = 120_000)]
	[MemberData(nameof(AllPlayers))]
	public async Task TheNextItem_Plays_AfterThePreviousOneEnded(MediaPlayerType playerType)
	{
		// this test hung the run without a result: every call runs with a limit and is announced on the console, which reaches the log
		// of the run also while the test host hangs
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);
		var player = await Within(playerType, "creating the player", () => CreatePlayer(playerType));
		try
		{
			var first = Item("first");
			var next = Item("next");
			var vm = CreateViewModel(player, [first, next], _ => Transcode(server.Url("hls-d0/index.m3u8")));

			vm.Playlist.SelectedItem = first;
			Assert.True(await Within(playerType, "loading the first item", () => vm.LoadSelectedItem().GetAwaiter().GetResult()));
			await WaitFor(() => player.Position >= TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));

			var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var endedSubscription = player.Ended.Subscribe(_ => ended.TrySetResult());
			await Within(playerType, "seeking near the end", () => player.SeekTo(TimeSpan.FromSeconds(10)));
			await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(20)));
			output.WriteLine($"{playerType}: ended={ended.Task.IsCompleted}, isPlaying={player.IsPlaying}, state={player.State}, position={player.Position:g}");
			Assert.True(ended.Task.IsCompleted, $"{playerType} did not report the end");

			vm.Playlist.SelectedItem = next;
			Assert.True(await Within(playerType, "loading the next item", () => vm.LoadSelectedItem().GetAwaiter().GetResult()));
			await WaitFor(() => player.State == MediaPlayerState.Playing && player.Position >= TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(20));

			output.WriteLine($"{playerType}: next item: isPlaying={player.IsPlaying}, state={player.State}, position={player.Position:g}");
			Assert.True(player.IsPlaying, $"{playerType} opened the next item paused");
			Assert.Equal(MediaPlayerState.Playing, player.State);
			Assert.True(player.Position >= TimeSpan.FromSeconds(1.5), $"{playerType} is at {player.Position} of the next item");
		}
		finally
		{
			await Within(playerType, "disposing the player", player.Dispose);
		}
	}

	private Task Within(MediaPlayerType playerType, string call, Action action) =>
		Within(playerType, call, () => { action(); return true; });

	/// <summary>Runs a call and fails the test when it does not return in 30 seconds, instead of hanging the whole run.</summary>
	private static async Task<T> Within<T>(MediaPlayerType playerType, string call, Func<T> action)
	{
		Console.WriteLine($"VideoPlayerReloadTests: {playerType}: {call} ({DateTime.UtcNow:HH:mm:ss.fff})");
		var task = Task.Run(action);
		if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) != task)
		{
			throw new TimeoutException($"{playerType}: {call} did not return in 30 s");
		}

		return await task;
	}

	/// <summary>
	/// The audio stream picked in the menu fails once the player tries to open it (the server answers 404 for the stream): picking it
	/// again asks the server again and plays from where the item was.
	/// </summary>
	[Theory(Timeout = 120_000)]
	[MemberData(nameof(AllPlayers))]
	public async Task AStreamThatFailsToOpen_CanBePickedAgain_AndResumesWhereTheItemWas(MediaPlayerType playerType)
	{
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);
		using var player = CreatePlayer(playerType);
		var item = Item("item");
		var requests = 0;
		var vm = CreateViewModel(player, [item], selection =>
		{
			var request = Interlocked.Increment(ref requests);
			return request switch
			{
				1 => Transcode(server.Url("hls-d0/index.m3u8")),
				2 => Transcode(server.Url("missing/index.m3u8")),
				_ => Transcode(server.Url("hls-d1/index.m3u8")),
			};
		});
		vm.SubscribeEvents(player);

		vm.Playlist.SelectedItem = item;
		Assert.True(await vm.LoadSelectedItem());
		await WaitFor(() => player.Position >= TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(20));
		var before = player.Position;

		var errored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var erroredSubscription = player.Errored.Subscribe(_ => errored.TrySetResult());
		Pick(player, 4);
		await WaitFor(() => Volatile.Read(ref requests) >= 2, TimeSpan.FromSeconds(10));
		await Task.WhenAny(errored.Task, Task.Delay(TimeSpan.FromSeconds(20)));
		// the handler of the error runs after the player reported it
		await Task.Delay(TimeSpan.FromSeconds(1));
		output.WriteLine($"{playerType}: before={before:g}, requests={requests}, errored={errored.Task.IsCompleted}, state={player.State}, selection={item.Selection}, error={player.LastError}");

		Pick(player, 4);
		await WaitFor(() => Volatile.Read(ref requests) >= 3, TimeSpan.FromSeconds(10));
		await WaitFor(() => player.State == MediaPlayerState.Playing && player.Position >= before, TimeSpan.FromSeconds(20));

		output.WriteLine($"{playerType}: after the second pick: requests={requests}, isPlaying={player.IsPlaying}, state={player.State}, position={player.Position:g}, selection={item.Selection}");
		var state = player.State;
		var position = player.Position;

		// leaving the page, as the app does, ends what the view model listens to before the player is disposed
		await vm.OnNavigatedFrom();

		Assert.Equal(3, Volatile.Read(ref requests));
		Assert.Equal(MediaPlayerState.Playing, state);
		Assert.True(position >= before - TimeSpan.FromSeconds(2), $"{playerType} plays at {position}, the item was at {before}");
	}

	/// <summary>A seek back to where the player already was once: the player goes there again.</summary>
	[Theory(Timeout = 120_000)]
	[MemberData(nameof(AllPlayers))]
	public async Task SeekingTwiceToTheSamePosition_GoesThereTwice(MediaPlayerType playerType)
	{
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);
		using var player = CreatePlayer(playerType);
		var target = TimeSpan.FromSeconds(2);
		// the time bar listens to the positions, which is how a player learns that it is at the target of a seek
		using var timeBar = player.PositionChanged.Subscribe(_ => { });

		Assert.True(player.Play(server.Url("tracks.mkv")));
		await WaitFor(() => player.Position >= TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));

		player.SeekTo(target);
		await WaitFor(() => player.Position >= target + TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(20));
		var reached = player.Position;

		player.SeekTo(target);
		await Task.Delay(TimeSpan.FromSeconds(2.5));
		var after = player.Position;

		output.WriteLine($"{playerType}: reached={reached:g}, 2.5 s after the second seek={after:g}, state={player.State}");
		Assert.True(after >= target + TimeSpan.FromSeconds(1) && after <= target + TimeSpan.FromSeconds(4),
			$"{playerType} is at {after}, 2.5 s after the second seek to {target} (it was at {reached})");
	}

	/// <summary>A seek before the start (back five seconds, one second after the start) goes to the start, and the time bar goes on.</summary>
	[Theory(Timeout = 120_000)]
	[MemberData(nameof(AllPlayers))]
	public async Task ASeekBeforeTheStart_GoesToTheStart_AndThePositionGoesOn(MediaPlayerType playerType)
	{
		using var server = new FakeMediaServer(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);
		using var player = CreatePlayer(playerType);
		var shown = new List<TimeSpan>();
		using var subscription = player.PositionChanged.Subscribe(x => { lock (shown) { shown.Add(x); } });

		Assert.True(player.Play(server.Url("tracks.mkv")));
		await WaitFor(() => player.Position >= TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
		lock (shown) { shown.Clear(); }

		player.SeekTo(player.Position - TimeSpan.FromSeconds(5));
		await Task.Delay(TimeSpan.FromSeconds(3));

		List<TimeSpan> given;
		lock (shown) { given = [.. shown]; }
		output.WriteLine($"{playerType}: position={player.Position:g}, the time bar was given: {string.Join(", ", given.Select(x => x.TotalSeconds.ToString("0.0")))}");
		Assert.All(given, x => Assert.True(x >= TimeSpan.Zero, $"{playerType} gave the time bar {x}"));
		Assert.True(player.Position >= TimeSpan.FromSeconds(1.5), $"{playerType} is at {player.Position}, 3 s after a seek to the start");
	}

	private static void Pick(IMediaPlayerController player, int streamIndex)
	{
		var menu = player.SourceAudioMenu ?? throw new InvalidOperationException("the player has no audio menu of the source");
		menu.Select(menu.Tracks.Single(x => x.Id == streamIndex));
	}

	private static PlaylistItem Item(string name) => new()
	{
		Title = name,
		Dto = new BaseItemDto { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Episode },
	};

	private static MediaResponse Transcode(Uri uri) =>
		new(uri, PlaybackProgressInfo_PlayMethod.Transcode, Guid.NewGuid().ToString("N"), "switch", Source());

	private static VideoPlayerViewModel CreateViewModel(IMediaPlayerController player, PlaylistItem[] items, Func<PlaybackSelection?, MediaResponse> media)
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://localhost");
		client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync((Guid id) => items.First(x => x.Dto.Id == id).Dto);
		client.Setup(x => x.GetMediaUrl(It.IsAny<BaseItemDto>(), It.IsAny<PlaybackSelection?>()))
			.ReturnsAsync((BaseItemDto _, PlaybackSelection? selection) => media(selection));

		var sessions = new Mock<IPlaybackSessionManager>();
		sessions.Setup(x => x.Begin(It.IsAny<Guid>(), It.IsAny<MediaResponse>()))
			.Returns((Guid id, MediaResponse response) => new PlaybackSession(id, response.PlaybackSessionId, response.MediaSourceId, response.PlayMethod));

		var profiles = new Mock<IDeviceProfileFactory>();
		profiles.Setup(x => x.GetDeviceProfile()).Returns(new DeviceProfile { DirectPlayProfiles = [], CodecProfiles = [] });

		var vm = new VideoPlayerViewModel(
			client.Object,
			new TrickplayViewModel(client.Object),
			NullLogger<VideoPlayerViewModel>.Instance,
			Observable.Empty<IOutboundWebSocketMessage>(),
			Mock.Of<INavigationService>(),
			sessions.Object,
			Mock.Of<ISettings>(),
			profiles.Object,
			Mock.Of<ITaskBarProgress>());

		var playlist = new PlaylistViewModel();
		foreach (var item in items)
		{
			playlist.Items.Add(item);
		}

		vm.Playlist = playlist;
		vm.MediaPlayer = player;
		return vm;
	}

	private static IMediaPlayerController CreatePlayer(MediaPlayerType type)
	{
		if (type == MediaPlayerType.Flyleaf)
		{
			FlyleafAudioPlaybackTests.StartEngine();
		}

		return type switch
		{
			MediaPlayerType.WindowsMediaPlayer => new WindowsMediaPlayerController(),
			MediaPlayerType.Vlc => new VlcMediaPlayerController(),
			MediaPlayerType.Flyleaf => new FlyleafMediaPlayerController(),
			_ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
		};
	}

	private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
	{
		var end = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < end && !condition())
		{
			await Task.Delay(50);
		}
	}
}
