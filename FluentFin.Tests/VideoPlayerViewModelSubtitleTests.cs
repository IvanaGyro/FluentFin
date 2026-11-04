using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

public sealed class VideoPlayerViewModelSubtitleTests
{
	private const string ServerUrl = "http://100.90.162.105:8096";

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<IMediaPlayerController> _player = new();

	static VideoPlayerViewModelSubtitleTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public VideoPlayerViewModelSubtitleTests()
	{
		_client.SetupGet(x => x.BaseUrl).Returns(ServerUrl);
	}

	[Fact]
	public async Task TranscodeWithWindowsMediaPlayer_LoadsTheEmbeddedSubtitleFromItsDeliveryUrl()
	{
		// a transcode with the profile of the Windows media player, which lists subrip as External only: the subtitles (streams 11 to 15
		// of a file with ten audio streams) are embedded in the file (IsExternal is false) and the server delivers them as files, the
		// transcoded stream has no subtitle track
		var subtitles = Enumerable.Range(11, 5)
			.Select(index => EmbeddedSubtitle(index, "External", $"/Videos/36e98edb/36e98edbf5ea353282a27966f62fb8f1/Subtitles/{index}/0/Stream.subrip?ApiKey=token"))
			.ToArray();
		var subtitle = subtitles[2];
		var response = Response(PlaybackProgressInfo_PlayMethod.Transcode, subtitles);

		await CreateViewModel().OpenSubtitlesWhenReady(_player.Object, ItemPlaying(response), response, subtitle);

		_player.Verify(x => x.OpenExternalSubtitleTrack(It.Is<string>(url => url.StartsWith(ServerUrl) && url.Contains("/Subtitles/13/0/Stream.subrip"))), Times.Once);
		_player.Verify(x => x.OpenInternalSubtitleTrack(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
	}

	[Fact]
	public async Task DirectPlay_OpensTheSubtitleOnlyOnceThePlayerHasLoadedItsTracks()
	{
		// 16 embedded subtitles (streams 2 to 17), the Japanese one (stream 10) is the 9th: the Windows media player and VLC know none of
		// the tracks right after Play() and all of them a moment later
		var subtitles = Enumerable.Range(2, 16).Select(index => EmbeddedSubtitle(index, "Embed", null)).ToArray();
		var japanese = subtitles[8];
		var response = Response(PlaybackProgressInfo_PlayMethod.DirectPlay, subtitles);

		var reported = new Queue<int>([0, 0, 0, 16]);
		var known = 0;
		_player.SetupGet(x => x.SubtitleTrackCount).Returns(() => known = reported.Count > 1 ? reported.Dequeue() : reported.Peek());

		var knownWhenOpened = new List<int>();
		_player.Setup(x => x.OpenInternalSubtitleTrack(10, 8)).Callback(() => knownWhenOpened.Add(known));

		await CreateViewModel().OpenSubtitlesWhenReady(_player.Object, ItemPlaying(response), response, japanese);

		Assert.Equal([16], knownWhenOpened);
	}

	[Fact]
	public async Task NoneSelected_DisablesTheSubtitlesAgainOnceThePlayerHasLoadedItsTracks()
	{
		// the players turn a subtitle on by themselves when they have loaded the tracks of the file
		var subtitles = Enumerable.Range(2, 3).Select(index => EmbeddedSubtitle(index, "Embed", null)).ToArray();
		var response = Response(PlaybackProgressInfo_PlayMethod.DirectPlay, subtitles);
		var item = ItemPlaying(response);
		item.Selection = new PlaybackSelection(null, PlaybackSelection.NoSubtitles);

		var reported = new Queue<int>([0, 0, 3]);
		var disabledWithTracks = new List<int>();
		var known = 0;
		_player.SetupGet(x => x.SubtitleTrackCount).Returns(() => known = reported.Count > 1 ? reported.Dequeue() : reported.Peek());
		_player.Setup(x => x.DisableSubtitles()).Callback(() => disabledWithTracks.Add(known));

		await CreateViewModel().DisableSubtitlesWhenReady(_player.Object, item, response, version: 0);

		// at once, and again after the tracks are known
		Assert.Equal([0, 3], disabledWithTracks);
	}

	[Fact]
	public async Task NoneSelected_AnotherItemStartedMeanwhile_DoesNotDisableTheSubtitlesOfTheNewItem()
	{
		var subtitles = Enumerable.Range(2, 3).Select(index => EmbeddedSubtitle(index, "Embed", null)).ToArray();
		var response = Response(PlaybackProgressInfo_PlayMethod.DirectPlay, subtitles);
		var item = ItemPlaying(response);
		item.Selection = new PlaybackSelection(null, PlaybackSelection.NoSubtitles);
		_player.SetupGet(x => x.SubtitleTrackCount).Returns(3);

		// the view model has loaded another item since: its load version is no longer the one of this task
		var viewModel = CreateViewModel();
		viewModel.GetType().GetField("_loadVersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(viewModel, 1);

		await viewModel.DisableSubtitlesWhenReady(_player.Object, item, response, version: 0);

		// only the immediate call of the task itself, none after the wait
		_player.Verify(x => x.DisableSubtitles(), Times.Once);
	}

	[Fact]
	public async Task NoneSelected_SubtitlePickedInThePlayerMenuMeanwhile_IsNotDisabled()
	{
		var subtitles = Enumerable.Range(2, 3).Select(index => EmbeddedSubtitle(index, "Embed", null)).ToArray();
		var response = Response(PlaybackProgressInfo_PlayMethod.DirectPlay, subtitles);
		var item = ItemPlaying(response);
		item.Selection = new PlaybackSelection(null, PlaybackSelection.NoSubtitles);

		// the user picks the subtitle of stream 3 from the player menu while the task waits for the tracks
		var calls = 0;
		_player.SetupGet(x => x.SubtitleTrackCount).Returns(() =>
		{
			if (++calls == 2)
			{
				item.Selection = new PlaybackSelection(null, 3);
			}

			return 0;
		});

		await CreateViewModel().DisableSubtitlesWhenReady(_player.Object, item, response, version: 0);

		_player.Verify(x => x.DisableSubtitles(), Times.Once);
	}

	[Fact]
	public void SubtitleMenu_ChecksNoneWhenNoneWasPickedOnThePage()
	{
		var source = new MediaSourceInfo { DefaultSubtitleStreamIndex = 4 };

		// the server's default subtitle is 4, the page picked "None"
		Assert.Equal(PlaybackSelection.NoSubtitles, FluentFin.Converters.Converters.ActiveSubtitleIndex(new PlaybackSelection(null, PlaybackSelection.NoSubtitles), source));
		Assert.Equal(7, FluentFin.Converters.Converters.ActiveSubtitleIndex(new PlaybackSelection(null, 7), source));
		Assert.Equal(4, FluentFin.Converters.Converters.ActiveSubtitleIndex(new PlaybackSelection(null, null), source));
		Assert.Equal(4, FluentFin.Converters.Converters.ActiveSubtitleIndex(null, source));
		Assert.Null(FluentFin.Converters.Converters.ActiveSubtitleIndex(null, new MediaSourceInfo()));
	}

	private VideoPlayerViewModel CreateViewModel() => new(
		_client.Object,
		new TrickplayViewModel(_client.Object),
		NullLogger<VideoPlayerViewModel>.Instance,
		Observable.Empty<IOutboundWebSocketMessage>(),
		Mock.Of<INavigationService>(),
		Mock.Of<IPlaybackSessionManager>(),
		Mock.Of<ISettings>(),
		Mock.Of<IDeviceProfileFactory>(),
		Mock.Of<ITaskBarProgress>());

	private static PlaylistItem ItemPlaying(MediaResponse response) => new()
	{
		Title = "Test",
		Dto = new BaseItemDto { Id = Guid.NewGuid() },
		Media = response,
	};

	/// <summary>The streams of a file as the server lists them: a video, the audio streams up to the first subtitle, then the subtitles.</summary>
	private static MediaResponse Response(PlaybackProgressInfo_PlayMethod playMethod, params MediaStream[] subtitles)
	{
		var video = new MediaStream { Index = 0, Type = MediaStream_Type.Video, Codec = "h264", IsExternal = false };
		var audio = Enumerable.Range(1, subtitles[0].Index!.Value - 1)
			.Select(index => new MediaStream { Index = index, Type = MediaStream_Type.Audio, Codec = "eac3", IsExternal = false });
		var source = new MediaSourceInfo { Id = "36e98edbf5ea353282a27966f62fb8f1", Container = "mkv", MediaStreams = [video, .. audio, .. subtitles] };

		// the server numbers the streams of a file one after the other and every stream has a codec

		var streams = source.MediaStreams!;
		Assert.Equal(Enumerable.Range(0, streams.Count), streams.Select(x => x.Index!.Value));
		Assert.All(streams, stream => Assert.False(string.IsNullOrEmpty(stream.Codec)));

		return new MediaResponse(new Uri(ServerUrl + "/videos/36e98edb/master.m3u8"), playMethod, "session", source.Id, source);
	}

	private static MediaStream EmbeddedSubtitle(int index, string deliveryMethod, string? deliveryUrl)
	{
		var stream = new MediaStream
		{
			Index = index,
			Type = MediaStream_Type.Subtitle,
			Codec = "subrip",
			IsExternal = false,
			DeliveryUrl = deliveryUrl,
		};

		// the enum of the delivery method is named differently in different versions of the generated client
		var property = typeof(MediaStream).GetProperty(nameof(MediaStream.DeliveryMethod))!;
		var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
		property.SetValue(stream, Enum.Parse(enumType, deliveryMethod));

		Assert.Equal(deliveryMethod, stream.DeliveryMethod?.ToString());

		return stream;
	}
}
