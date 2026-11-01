using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

public sealed class PlaybackInfoTests
{
	[Fact]
	public void TranscodeReasons_AreReadFromTheTranscodingUrl()
	{
		var reasons = PlaybackInfo.TranscodeReasons("/videos/1/master.m3u8?PlaySessionId=p&TranscodeReasons=AudioCodecNotSupported,ContainerBitrateExceedsLimit");

		Assert.Equal(["AudioCodecNotSupported", "ContainerBitrateExceedsLimit"], reasons);
	}

	[Fact]
	public void TranscodeReasons_AreEmptyWithoutATranscodingUrl()
	{
		Assert.Empty(PlaybackInfo.TranscodeReasons(null));
		Assert.Empty(PlaybackInfo.TranscodeReasons("/videos/1/stream"));
	}

	[Fact]
	public void Humanize_SplitsPascalCase() =>
		Assert.Equal("Audio codec not supported", PlaybackInfo.Humanize("AudioCodecNotSupported"));

	[Fact]
	public void FormatBitrate_UsesMbpsAndKbps()
	{
		Assert.Equal("8 Mbps", PlaybackInfo.FormatBitrate(8_000_000L));
		Assert.Equal("640 kbps", PlaybackInfo.FormatBitrate(640_000L));
		Assert.Null(PlaybackInfo.FormatBitrate(0L));
	}

	[Fact]
	public void Build_ForTranscode_ListsTranscodingReasonsAndSelectedStreams()
	{
		var source = new MediaSourceInfo
		{
			Container = "mkv",
			TranscodingUrl = "/videos/1/master.m3u8?TranscodeReasons=AudioCodecNotSupported",
			MediaStreams =
			[
				new MediaStream { Type = MediaStream_Type.Video, Index = 0, Codec = "hevc", Width = 1920, Height = 1080 },
				new MediaStream { Type = MediaStream_Type.Audio, Index = 1, Codec = "dts", Language = "eng" },
				new MediaStream { Type = MediaStream_Type.Audio, Index = 2, Codec = "aac", Language = "jpn" },
			],
		};
		var media = new MediaResponse(new Uri("http://server/x"), PlaybackProgressInfo_PlayMethod.Transcode, "session", "source", source);

		var sections = PlaybackInfo.Build(media, audioStreamIndex: 2, subtitleStreamIndex: -1, playerName: "Vlc");

		Assert.Equal(["Playback info", "Transcoding info", "Video", "Audio"], sections.Select(x => x.Title));
		Assert.Contains(sections[1].Rows, x => x is { Label: "Reason", Value: "Audio codec not supported" });
		Assert.Contains(sections[2].Rows, x => x is { Label: "Resolution", Value: "1920x1080" });
		Assert.Contains(sections[3].Rows, x => x is { Label: "Codec", Value: "AAC" });
	}

	[Fact]
	public void Build_ForTranscode_PutsEveryReasonOnItsOwnLineAndShowsTheTranscodedStream()
	{
		var source = new MediaSourceInfo
		{
			TranscodingUrl = "/videos/1/master.m3u8?TranscodeReasons=AudioCodecNotSupported,ContainerBitrateExceedsLimit",
		};
		var media = new MediaResponse(new Uri("http://server/x"), PlaybackProgressInfo_PlayMethod.Transcode, "s", "m", source);
		var live = new TranscodingInfo { Container = "ts", VideoCodec = "h264", IsVideoDirect = false, AudioCodec = "aac", IsAudioDirect = true, Width = 1280, Height = 720, Bitrate = 4_000_000 };

		var rows = PlaybackInfo.Build(media, null, null, null, live).Single(x => x.Title == "Transcoding info").Rows;

		Assert.Contains(rows, x => x is { Label: "Reasons", Value: "Audio codec not supported\nContainer bitrate exceeds limit" });
		Assert.Contains(rows, x => x is { Label: "Video", Value: "H264 (transcoded)" });
		Assert.Contains(rows, x => x is { Label: "Audio", Value: "AAC (direct)" });
		Assert.Contains(rows, x => x is { Label: "Resolution", Value: "1280x720" });
	}

	[Theory]
	[InlineData(PlaybackProgressInfo_PlayMethod.DirectPlay, null, null, "Direct play")]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode, null, null, "Transcoding")]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode, false, false, "Transcoding")]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode, false, true, "Transcoding")]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode, true, true, "Direct stream")]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode, true, false, "Direct stream (audio transcoded)")]
	public void DescribePlayMethod_TellsDirectStreamFromTranscode(PlaybackProgressInfo_PlayMethod method, bool? videoDirect, bool? audioDirect, string expected)
	{
		var live = videoDirect is null ? null : new TranscodingInfo { IsVideoDirect = videoDirect, IsAudioDirect = audioDirect };

		Assert.Equal(expected, PlaybackInfo.DescribePlayMethod(method, live));
	}

	[Fact]
	public void Build_ForDirectPlay_HasNoTranscodingSection()
	{
		var media = new MediaResponse(new Uri("http://server/x"), PlaybackProgressInfo_PlayMethod.DirectPlay, "s", "m", new MediaSourceInfo { Container = "mp4" });

		var sections = PlaybackInfo.Build(media, null, null, null);

		Assert.DoesNotContain(sections, x => x.Title == "Transcoding info");
	}
}
