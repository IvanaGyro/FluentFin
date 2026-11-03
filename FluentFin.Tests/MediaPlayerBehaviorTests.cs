using FluentFin.Core.Contracts.Services;
using FluentFin.MediaPlayers;
using ReactiveUI;
using Xunit.Abstractions;

namespace FluentFin.Tests;

/// <summary>
/// Behavioral boundary tests across all three media player controllers (<see cref="WindowsMediaPlayerController"/>,
/// <see cref="VlcMediaPlayerController"/>, and <see cref="FlyleafMediaPlayerController"/>) through the public
/// <see cref="IMediaPlayerController"/> contract:
/// <list type="bullet">
/// <item><description>Switching streams while playing keeps <see cref="IMediaPlayerController.IsPlaying"/> true, emits no <see cref="IMediaPlayerController.Paused"/> events, and keeps the timeline at the resume position without flashing to 0:00:00.</description></item>
/// <item><description>Pressing Pause while waiting for the server or while a stream is opening flips <see cref="IMediaPlayerController.IsPlaying"/> to false immediately and keeps the backend paused at the target position once loaded.</description></item>
/// <item><description>Pressing Pause and then Play again during a switch or load flips the UI state and results in normal playback once ready.</description></item>
/// <item><description>Switching streams while already paused stays paused at the target position without emitting <see cref="IMediaPlayerController.Playing"/>.</description></item>
/// <item><description>Seeking while playing never flickers <see cref="IMediaPlayerController.IsPlaying"/> or emits <see cref="IMediaPlayerController.Paused"/>.</description></item>
/// </list>
/// </summary>
[Collection(AudioDevice.Name)]
public sealed class MediaPlayerBehaviorTests(ITestOutputHelper output)
{
	private static readonly TimeSpan ServerDelay = TimeSpan.FromMilliseconds(50);
	private static readonly TimeSpan ServerAnswer = TimeSpan.FromMilliseconds(400);
	private static readonly TimeSpan Target = TimeSpan.FromSeconds(6);

	public static TheoryData<MediaPlayerType> AllPlayers => new()
	{
		MediaPlayerType.WindowsMediaPlayer,
		MediaPlayerType.Vlc,
		MediaPlayerType.Flyleaf,
	};

	[Theory]
	[MemberData(nameof(AllPlayers))]
	public async Task SwitchingStream_WhilePlaying_KeepsPlayStateAndTimelineWithoutFlickering(MediaPlayerType playerType)
	{
		using var server = CreateServer();
		using var controller = CreateController(playerType);

		var playingEvents = 0;
		var pausedEvents = 0;
		var positions = new List<TimeSpan>();
		var durations = new List<TimeSpan>();

		Assert.True(controller.Play(server.Url("tracks.mkv")));
		Assert.True(controller.IsPlaying);
		await WaitFor(() => controller.Position >= TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(15));

		using var playSub = controller.Playing.Subscribe(_ => Interlocked.Increment(ref playingEvents));
		using var pauseSub = controller.Paused.Subscribe(_ => Interlocked.Increment(ref pausedEvents));
		using var posSub = controller.PositionChanged.Subscribe(p => { lock (positions) { positions.Add(p); } });
		using var durSub = controller.DurationChanged.Subscribe(d => { lock (durations) { durations.Add(d); } });

		// Simulate switching audio stream: pause backend in place and hold position while waiting for server
		controller.HoldPosition(Target);
		Assert.True(controller.IsPlaying);
		Assert.Equal(Target, controller.Position);
		await Task.Delay(ServerAnswer);
		Assert.True(controller.IsPlaying);
		Assert.Equal(0, Volatile.Read(ref pausedEvents));

		// Server responds with the transcoded stream; open and seek to Target
		Assert.True(controller.Play(server.Url("hls-d0/index.m3u8")));
		controller.SeekTo(Target);
		Assert.True(controller.IsPlaying);

		await WaitFor(() => controller.Position >= Target + TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(20));

		List<TimeSpan> recordedPositions;
		List<TimeSpan> recordedDurations;
		lock (positions) { recordedPositions = [.. positions]; }
		lock (durations) { recordedDurations = [.. durations]; }

		output.WriteLine($"{playerType}: position={controller.Position:g}, playingEvents={playingEvents}, pausedEvents={pausedEvents}, positions=[{string.Join(", ", recordedPositions.Select(x => x.TotalSeconds.ToString("0.0")))}]");

		Assert.True(controller.IsPlaying);
		Assert.Equal(0, Volatile.Read(ref pausedEvents));
		Assert.Equal(0, Volatile.Read(ref playingEvents));
		Assert.NotEmpty(recordedPositions);
		Assert.All(recordedPositions, p => Assert.True(p >= Target - TimeSpan.FromSeconds(2), $"{playerType} reported early position {p}"));
		Assert.All(recordedDurations, d => Assert.True(d > TimeSpan.Zero, $"{playerType} reported zero duration {d}"));
	}

	[Theory]
	[MemberData(nameof(AllPlayers))]
	public async Task PausingDuringStreamSwitch_WhileWaitingForServer_KeepsPlayerPausedOnceLoaded(MediaPlayerType playerType)
	{
		using var server = CreateServer();
		using var controller = CreateController(playerType);

		Assert.True(controller.Play(server.Url("tracks.mkv")));
		await WaitFor(() => controller.Position >= TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(15));

		var playingEvents = 0;
		var pausedEvents = 0;
		using var playSub = controller.Playing.Subscribe(_ => Interlocked.Increment(ref playingEvents));
		using var pauseSub = controller.Paused.Subscribe(_ => Interlocked.Increment(ref pausedEvents));

		// Begin stream switch (waiting for server)
		controller.HoldPosition(Target);
		await Task.Delay(100);

		// User clicks Pause while waiting for the server
		await controller.TogglePlayPlause(null!);
		Assert.False(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref pausedEvents));

		await Task.Delay(ServerAnswer);

		// Server responds and view model opens the new stream at Target
		Assert.True(controller.Play(server.Url("hls-d0/index.m3u8")));
		controller.SeekTo(Target);

		Assert.False(controller.IsPlaying);
		Assert.Equal(0, Volatile.Read(ref playingEvents));

		// Let the stream finish opening and seeking to Target, then verify playback does not advance
		await Task.Delay(TimeSpan.FromSeconds(1.5));
		var settled = controller.Position;
		await Task.Delay(TimeSpan.FromSeconds(1.0));
		var afterWait = controller.Position;

		output.WriteLine($"{playerType}: settled={settled:g}, afterWait={afterWait:g}, isPlaying={controller.IsPlaying}, state={controller.State}");

		Assert.False(controller.IsPlaying);
		Assert.Equal(0, Volatile.Read(ref playingEvents));
		Assert.Equal(1, Volatile.Read(ref pausedEvents));
		Assert.True(afterWait >= Target - TimeSpan.FromSeconds(2), $"{playerType} position {afterWait} is before {Target}");
		Assert.True((afterWait - settled).Duration() < TimeSpan.FromSeconds(0.5), $"{playerType} advanced from {settled} to {afterWait} while paused");
	}

	[Theory]
	[MemberData(nameof(AllPlayers))]
	public async Task PausingWhileOpeningAndSeeking_StaysPausedOnceReady(MediaPlayerType playerType)
	{
		using var server = CreateServer();
		using var controller = CreateController(playerType);

		var playingEvents = 0;
		var pausedEvents = 0;
		using var playSub = controller.Playing.Subscribe(_ => Interlocked.Increment(ref playingEvents));
		using var pauseSub = controller.Paused.Subscribe(_ => Interlocked.Increment(ref pausedEvents));

		Assert.True(controller.Play(server.Url("hls-d0/index.m3u8")));
		controller.SeekTo(Target);
		Assert.True(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref playingEvents));

		// User clicks Pause immediately while the stream is still opening/seeking
		await controller.TogglePlayPlause(null!);
		Assert.False(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref pausedEvents));

		await Task.Delay(TimeSpan.FromSeconds(1.5));
		var settled = controller.Position;
		await Task.Delay(TimeSpan.FromSeconds(1.0));
		var afterWait = controller.Position;

		output.WriteLine($"{playerType}: settled={settled:g}, afterWait={afterWait:g}, isPlaying={controller.IsPlaying}, state={controller.State}");

		Assert.False(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref playingEvents));
		Assert.Equal(1, Volatile.Read(ref pausedEvents));
		Assert.True(afterWait >= Target - TimeSpan.FromSeconds(2), $"{playerType} position {afterWait} is before {Target}");
		Assert.True((afterWait - settled).Duration() < TimeSpan.FromSeconds(0.5), $"{playerType} advanced from {settled} to {afterWait} while paused");
	}

	[Theory]
	[MemberData(nameof(AllPlayers))]
	public async Task PausingThenPlayingAgain_DuringStreamSwitch_ResumesPlaybackNormally(MediaPlayerType playerType)
	{
		using var server = CreateServer();
		using var controller = CreateController(playerType);

		Assert.True(controller.Play(server.Url("tracks.mkv")));
		await WaitFor(() => controller.Position >= TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(15));

		var playingEvents = 0;
		var pausedEvents = 0;
		var positions = new List<TimeSpan>();
		using var playSub = controller.Playing.Subscribe(_ => Interlocked.Increment(ref playingEvents));
		using var pauseSub = controller.Paused.Subscribe(_ => Interlocked.Increment(ref pausedEvents));
		using var posSub = controller.PositionChanged.Subscribe(p => { lock (positions) { positions.Add(p); } });

		controller.HoldPosition(Target);

		// User clicks Pause while waiting for server
		await controller.TogglePlayPlause(null!);
		Assert.False(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref pausedEvents));

		await Task.Delay(150);

		// Server opens the new stream, and user clicks Play again while it is opening
		Assert.True(controller.Play(server.Url("hls-d0/index.m3u8")));
		controller.SeekTo(Target);
		Assert.False(controller.IsPlaying);

		await controller.TogglePlayPlause(null!);
		Assert.True(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref playingEvents));

		await WaitFor(() => controller.Position >= Target + TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(20));

		List<TimeSpan> recordedPositions;
		lock (positions) { recordedPositions = [.. positions]; }

		output.WriteLine($"{playerType}: position={controller.Position:g}, playingEvents={playingEvents}, pausedEvents={pausedEvents}");

		Assert.True(controller.IsPlaying);
		Assert.Equal(1, Volatile.Read(ref pausedEvents));
		Assert.Equal(1, Volatile.Read(ref playingEvents));
		Assert.All(recordedPositions, p => Assert.True(p >= Target - TimeSpan.FromSeconds(2), $"{playerType} reported early position {p}"));
	}

	[Theory]
	[MemberData(nameof(AllPlayers))]
	public async Task SwitchingStream_WhileAlreadyPaused_StaysPausedWithoutEmittingPlaying(MediaPlayerType playerType)
	{
		// This test hung the test run (no result for hours) and the run does not say for which player or at which call: every call of the
		// player runs with a limit, a call that does not return fails the test with its name
		using var server = CreateServer();
		var controller = await Within(playerType, "creating the player", () => CreateController(playerType));
		try
		{
			Assert.True(await Within(playerType, "Play(tracks.mkv)", () => controller.Play(server.Url("tracks.mkv"))));
			await WaitFor(() => controller.Position >= TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(15));

			await Within(playerType, "TogglePlayPlause (pause)", () => controller.TogglePlayPlause(null!).AsTask().Wait());
			Assert.False(controller.IsPlaying);

			var playingEvents = 0;
			var pausedEvents = 0;
			using var playSub = controller.Playing.Subscribe(_ => Interlocked.Increment(ref playingEvents));
			using var pauseSub = controller.Paused.Subscribe(_ => Interlocked.Increment(ref pausedEvents));

			await Within(playerType, "HoldPosition", () => controller.HoldPosition(Target));
			await Task.Delay(ServerAnswer);

			Assert.True(await Within(playerType, "Play(hls-d0) while paused", () => controller.Play(server.Url("hls-d0/index.m3u8"))));
			await Within(playerType, "SeekTo", () => controller.SeekTo(Target));

			Assert.False(controller.IsPlaying);
			Assert.Equal(0, Volatile.Read(ref playingEvents));
			Assert.Equal(0, Volatile.Read(ref pausedEvents));

			await Task.Delay(TimeSpan.FromSeconds(1.5));
			var settled = await Within(playerType, "Position", () => controller.Position);
			await Task.Delay(TimeSpan.FromSeconds(1.0));
			var afterWait = await Within(playerType, "Position", () => controller.Position);

			output.WriteLine($"{playerType}: settled={settled:g}, afterWait={afterWait:g}, isPlaying={controller.IsPlaying}, state={controller.State}");

			Assert.False(controller.IsPlaying);
			Assert.Equal(0, Volatile.Read(ref playingEvents));
			Assert.Equal(0, Volatile.Read(ref pausedEvents));
			Assert.True(afterWait >= Target - TimeSpan.FromSeconds(2), $"{playerType} position {afterWait} is before {Target}");
			Assert.True((afterWait - settled).Duration() < TimeSpan.FromSeconds(0.5), $"{playerType} advanced from {settled} to {afterWait} while paused");
		}
		finally
		{
			await Within(playerType, "Dispose", controller.Dispose);
		}
	}

	/// <summary>
	/// The first <see cref="FlyleafMediaPlayerController"/> of the run did not return from its constructor in 30 s, with no other Flyleaf
	/// player alive, while a plain Flyleaf player is created by the audio tests. The steps of the constructor one by one, each with a limit,
	/// name the one that does not return.
	/// </summary>
	[Fact]
	public async Task Flyleaf_TheStepsOfTheController_Return()
	{
		await Within(MediaPlayerType.Flyleaf, "starting the engine", FlyleafAudioPlaybackTests.StartEngine);
		using var player = await Within(MediaPlayerType.Flyleaf, "new Player()", () => new FlyleafLib.MediaPlayer.Player());
		await Within(MediaPlayerType.Flyleaf, "KeyBindings.RemoveAll", () => player.Config.Player.KeyBindings.RemoveAll());
		await Within(MediaPlayerType.Flyleaf, "reading Status", () => player.Status);
		using var status = await Within(MediaPlayerType.Flyleaf, "WhenAnyValue(Status)", () => player.WhenAnyValue(x => x.Status).Subscribe(_ => { }));
		using var time = await Within(MediaPlayerType.Flyleaf, "WhenAnyValue(CurTime)", () => player.WhenAnyValue(x => x.CurTime).Subscribe(_ => { }));
		var controller = await Within(MediaPlayerType.Flyleaf, "new FlyleafMediaPlayerController()", () => new FlyleafMediaPlayerController());
		await Within(MediaPlayerType.Flyleaf, "Dispose of the controller", controller.Dispose);
	}

	/// <summary>
	/// The steps of the constructor of <see cref="FlyleafMediaPlayerController"/>, each announced on the console before it runs: the console
	/// reaches the log of the run at once, also when the test host hangs, and the last line names the step that does not return.
	/// </summary>
	private static void TraceFlyleafSteps()
	{
		static void Step(string name) => Console.WriteLine($"MediaPlayerBehaviorTests: Flyleaf step: {name} (thread {Environment.CurrentManagedThreadId}, {DateTime.UtcNow:HH:mm:ss.fff})");

		Step("new Player()");
		using var player = new FlyleafLib.MediaPlayer.Player();
		Step("KeyBindings.RemoveAll");
		player.Config.Player.KeyBindings.RemoveAll();
		Step("reading Status");
		_ = player.Status;
		Step("WhenAnyValue(Status)");
		using (player.WhenAnyValue(x => x.Status).Subscribe(_ => { }))
		{
			Step("WhenAnyValue(CurTime)");
			using (player.WhenAnyValue(x => x.CurTime).Subscribe(_ => { }))
			{
				Step("disposing the player");
			}
		}
	}

	private Task Within(MediaPlayerType playerType, string call, Action action) =>
		Within(playerType, call, () => { action(); return true; });

	/// <summary>Runs a call of the player and fails the test when it does not return in 30 seconds, instead of hanging the whole run.</summary>
	private async Task<T> Within<T>(MediaPlayerType playerType, string call, Func<T> action)
	{
		var started = DateTime.UtcNow;
		var task = Task.Run(action);
		if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))) != task)
		{
			throw new TimeoutException($"{playerType}: {call} did not return in 30 s. {FlyleafPlayers()}");
		}

		var result = await task;
		var took = DateTime.UtcNow - started;
		if (took > TimeSpan.FromSeconds(1))
		{
			output.WriteLine($"{playerType}: {call} took {took.TotalSeconds:0.0} s");
		}

		return result;
	}

	[Theory]
	[MemberData(nameof(AllPlayers))]
	public async Task Seeking_WhilePlaying_NeverEmitsPausedOrFlipsIsPlaying(MediaPlayerType playerType)
	{
		using var server = CreateServer();
		using var controller = CreateController(playerType);

		Assert.True(controller.Play(server.Url("tracks.mkv")));
		await WaitFor(() => controller.Position >= TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(15));

		var playingEvents = 0;
		var pausedEvents = 0;
		using var playSub = controller.Playing.Subscribe(_ => Interlocked.Increment(ref playingEvents));
		using var pauseSub = controller.Paused.Subscribe(_ => Interlocked.Increment(ref pausedEvents));

		controller.SeekTo(Target);
		Assert.True(controller.IsPlaying);

		await WaitFor(() => controller.Position >= Target + TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(15));

		Assert.True(controller.IsPlaying);
		Assert.Equal(0, Volatile.Read(ref pausedEvents));
		Assert.Equal(0, Volatile.Read(ref playingEvents));
	}

	/// <summary>The players of Flyleaf that are not disposed: one left by an earlier test keeps decoding and can block the next one.</summary>
	internal static string FlyleafPlayers()
	{
		try
		{
			var players = FlyleafLib.Engine.IsLoaded ? FlyleafLib.Engine.Players.ToList() : [];
			return $"Flyleaf players not disposed: {players.Count}" + string.Concat(players.Select(p => $"; #{p.PlayerId} {p.Status} at {TimeSpan.FromTicks(p.CurTime)}"));
		}
		catch (Exception ex)
		{
			return "Flyleaf players: " + ex.Message;
		}
	}

	private static FakeMediaServer CreateServer() =>
		new(Path.Combine(AppContext.BaseDirectory, "Media", "switch"), ServerDelay);

	private static IMediaPlayerController CreateController(MediaPlayerType type)
	{
		if (type == MediaPlayerType.Flyleaf)
		{
			FlyleafAudioPlaybackTests.StartEngine();
			Console.WriteLine($"MediaPlayerBehaviorTests: creating a Flyleaf player. {FlyleafPlayers()}");
			TraceFlyleafSteps();
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
