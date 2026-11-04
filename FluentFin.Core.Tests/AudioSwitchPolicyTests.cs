using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

public sealed class AudioSwitchPolicyTests
{
	private static readonly MediaSourceInfo Source = new()
	{
		Container = "mkv",
		MediaStreams =
		[
			new MediaStream { Index = 0, Type = MediaStream_Type.Video, Codec = "h264" },
			new MediaStream { Index = 1, Type = MediaStream_Type.Audio, Codec = "dts" },
			new MediaStream { Index = 2, Type = MediaStream_Type.Audio, Codec = "AAC" },
		],
	};

	private static readonly DeviceProfile Profile = new()
	{
		DirectPlayProfiles =
		[
			new DirectPlayProfile { Type = DirectPlayProfile_Type.Audio, Container = "mp3", AudioCodec = "dts" },
			new DirectPlayProfile { Type = DirectPlayProfile_Type.Video, Container = "mp4, mkv", AudioCodec = "aac,ac3" },
		],
	};

	[Fact]
	public void ADirectPlay_SwitchesInThePlayer_ToACodecOfTheProfile()
	{
		Assert.True(AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, 2, Profile));
	}

	[Fact]
	public void ADirectPlay_AsksTheServer_ForACodecTheProfileDoesNotPlay()
	{
		// the server decides: it transcodes the stream
		Assert.False(AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, 1, Profile));
	}

	[Theory]
	[InlineData(PlaybackProgressInfo_PlayMethod.Transcode)]
	[InlineData(PlaybackProgressInfo_PlayMethod.DirectStream)]
	public void AStreamOfTheServer_AsksTheServer(PlaybackProgressInfo_PlayMethod playMethod)
	{
		Assert.False(AudioSwitchPolicy.CanSwitchInPlayer(playMethod, Source, 2, Profile));
	}

	[Fact]
	public void AContainerOutsideTheProfile_AsksTheServer()
	{
		var source = new MediaSourceInfo { Container = "avi", MediaStreams = Source.MediaStreams };

		Assert.False(AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, source, 2, Profile));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(42)]
	public void WithoutAnAudioStream_AsksTheServer(int index)
	{
		Assert.False(AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, Source, index, Profile));
	}

	private static DeviceProfile WithAacLimits(bool required = false) => new()
	{
		DirectPlayProfiles = Profile.DirectPlayProfiles,
		CodecProfiles =
		[
			new CodecProfile
			{
				Type = CodecProfile_Type.VideoAudio,
				Codec = "aac",
				Conditions =
				[
					new ProfileCondition { Condition = ProfileCondition_Condition.LessThanEqual, Property = ProfileCondition_Property.AudioChannels, Value = "6", IsRequired = required },
					new ProfileCondition { Condition = ProfileCondition_Condition.LessThanEqual, Property = ProfileCondition_Property.AudioSampleRate, Value = "48000", IsRequired = required },
				],
			},
		],
	};

	private static MediaSourceInfo SourceWithAac(int? channels, int? sampleRate) => new()
	{
		Container = "mkv",
		MediaStreams = [new MediaStream { Index = 2, Type = MediaStream_Type.Audio, Codec = "aac", Channels = channels, SampleRate = sampleRate }],
	};

	[Theory]
	[InlineData(6, 48000, true)]
	[InlineData(2, 44100, true)]
	[InlineData(8, 48000, false)]
	[InlineData(6, 96000, false)]
	public void TheLimitsOfTheCodecProfile_DecideWhetherThePlayerSwitches(int channels, int sampleRate, bool expected)
	{
		Assert.Equal(expected, AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, SourceWithAac(channels, sampleRate), 2, WithAacLimits()));
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void AStreamWithoutTheValue_SwitchesInThePlayerUnlessTheProfileRequiresIt(bool required, bool expected)
	{
		Assert.Equal(expected, AudioSwitchPolicy.CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod.DirectPlay, SourceWithAac(null, null), 2, WithAacLimits(required)));
	}
}
