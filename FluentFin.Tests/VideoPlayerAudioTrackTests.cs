using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

public sealed class VideoPlayerAudioTrackTests
{
	// a file like the one of the report: video, ten audio streams (1 to 10), then subtitles
	private static readonly MediaSourceInfo Source = new()
	{
		MediaStreams =
		[
			new MediaStream { Index = 0, Type = MediaStream_Type.Video },
			.. Enumerable.Range(1, 10).Select(index => new MediaStream { Index = index, Type = MediaStream_Type.Audio }),
			new MediaStream { Index = 11, Type = MediaStream_Type.Subtitle },
		],
	};

	[Fact]
	public void DirectPlay_SwitchesToTheAudioStreamByItsPositionAmongTheAudioTracks()
	{
		// stream 6 is the sixth audio track of the file
		Assert.Equal(5, VideoPlayerViewModel.DirectPlayAudioOrdinal(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, 6));
	}

	[Fact]
	public void DirectPlay_SwitchesToTheServerDefaultToo()
	{
		// without a choice on the page the server's DefaultAudioStreamIndex is passed, it may not be the first track of the file
		Assert.Equal(2, VideoPlayerViewModel.DirectPlayAudioOrdinal(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, 3));
	}

	[Theory]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode)]
	[InlineData(PlaybackProgressInfo_PlayMethod.DirectStream)]
	public void TheServer_CarriesTheAudioStream_WhenItDoesNotDirectPlay(PlaybackProgressInfo_PlayMethod playMethod)
	{
		Assert.Null(VideoPlayerViewModel.DirectPlayAudioOrdinal(playMethod, Source, 6));
	}

	[Theory]
	[InlineData(null)]
	[InlineData(11)]
	[InlineData(42)]
	public void NoSwitch_WithoutAnAudioStream(int? audioStreamIndex)
	{
		Assert.Null(VideoPlayerViewModel.DirectPlayAudioOrdinal(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, audioStreamIndex));
	}

	[Fact]
	public void SourceMenu_ListsTheAudioStreamsOfTheSourceByStreamIndex()
	{
		var tracks = VideoPlayerViewModel.BuildSourceAudioTracks(Source);

		Assert.Equal(Enumerable.Range(1, 10), tracks.Select(x => x.Id));
	}

	[Theory]
	[InlineData("zho", "Chinese")]
	[InlineData("eng", "English")]
	[InlineData("xxx", "xxx")]
	[InlineData(null, null)]
	public void SourceMenu_NamesTheLanguageLikeThePlayersDo(string? code, string? expected)
	{
		Assert.Equal(expected, VideoPlayerViewModel.LanguageName(code));
	}

	[Fact]
	public void SourceMenu_IsBuiltForDirectPlayToo_SoThatTheWayBackToATranscodeExists()
	{
		var picked = new List<int>();
		var menu = VideoPlayerViewModel.BuildSourceAudioMenu(Source, 2, picked.Add);

		Assert.NotNull(menu);
		Assert.Equal(2, menu!.SelectedId);

		menu.Select(menu.Tracks[5]);
		Assert.Equal([6], picked);
	}

	[Fact]
	public void SourceMenu_DoesNotReloadForTheStreamThatPlays()
	{
		var picked = new List<int>();
		var menu = VideoPlayerViewModel.BuildSourceAudioMenu(Source, 3, picked.Add)!;

		menu.Select(menu.Tracks[2]);
		Assert.Empty(picked);
	}

	[Fact]
	public void SourceMenu_FallsBackToTheFirstStream_WhenTheServerGaveNone()
	{
		Assert.Equal(1, VideoPlayerViewModel.BuildSourceAudioMenu(Source, null, _ => { })!.SelectedId);
		Assert.Equal(1, VideoPlayerViewModel.BuildSourceAudioMenu(Source, 42, _ => { })!.SelectedId);
	}

	[Fact]
	public void SourceMenu_IsNotShown_ForASingleAudioStream()
	{
		var single = new MediaSourceInfo { MediaStreams = [new MediaStream { Index = 1, Type = MediaStream_Type.Audio }] };

		Assert.Null(VideoPlayerViewModel.BuildSourceAudioMenu(single, 1, _ => { }));
	}
}
