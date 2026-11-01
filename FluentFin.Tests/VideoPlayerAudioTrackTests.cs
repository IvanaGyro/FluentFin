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
}
