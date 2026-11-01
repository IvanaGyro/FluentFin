using Jellyfin.Sdk.Generated.Models;
using Xunit.Abstractions;

namespace FluentFin.Tests;

public sealed class WmpDeviceProfileManagerTests(ITestOutputHelper output)
{
	private static readonly Guid[] InBoxVideo = [WmpSubtypes.H264, WmpSubtypes.Mpeg2, WmpSubtypes.Vc1];
	private static readonly Guid[] InBoxAudio = [WmpSubtypes.Aac, WmpSubtypes.Mp3, WmpSubtypes.Mpeg, WmpSubtypes.Ac3, WmpSubtypes.Eac3, WmpSubtypes.Flac, WmpSubtypes.Alac, WmpSubtypes.WmaV9];

	[Fact]
	public void AllVideo_IsLimitedTo8K()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities());

		var global = profile.CodecProfiles!.Single(p => p.Type == CodecProfile_Type.Video && p.Codec is null);
		Assert.Equal("8192", Condition(global, ProfileCondition_Property.Width).Value);
	}

	[Fact]
	public void HevcDecodedIn8KByTheGpu_IsDirectPlayedUpTo8K()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(
			video: [.. InBoxVideo, WmpSubtypes.Hevc],
			hardware: new() { [HardwareDecoder.Hevc] = new(8192, 4320), [HardwareDecoder.Hevc10Bit] = new(8192, 4320) }));

		var hevc = CodecProfile(profile, "hevc");
		Assert.Equal("8192", Condition(hevc, ProfileCondition_Property.Width).Value);
		Assert.Equal("186", Condition(hevc, ProfileCondition_Property.VideoLevel).Value);
		Assert.Contains("main 10", Condition(hevc, ProfileCondition_Property.VideoProfile).Value);
		Assert.DoesNotContain(profile.CodecProfiles!, p => p.Codec == "hevc" && p.ApplyConditions is { Count: > 0 });
	}

	[Fact]
	public void HevcWithoutGpuDecoding_IsLimitedToTheDocumentedSoftwareDecoder()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(video: [.. InBoxVideo, WmpSubtypes.Hevc]));

		var hevc = CodecProfile(profile, "hevc");
		Assert.Equal("4096", Condition(hevc, ProfileCondition_Property.Width).Value);
		Assert.Equal("156", Condition(hevc, ProfileCondition_Property.VideoLevel).Value);
		Assert.Equal("4096", Condition(CodecProfile(profile, "h264"), ProfileCondition_Property.Width).Value);
		Assert.Equal("51", Condition(CodecProfile(profile, "h264"), ProfileCondition_Property.VideoLevel).Value);
	}

	[Fact]
	public void TenBitVideo_IsLimitedToWhatTheGpuDecodesIn10Bit()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(
			video: [.. InBoxVideo, WmpSubtypes.Hevc],
			hardware: new() { [HardwareDecoder.Hevc] = new(8192, 4320) }));

		var tenBit = profile.CodecProfiles!.Single(p => p.Codec == "hevc" && p.ApplyConditions is { Count: > 0 });
		Assert.Equal(ProfileCondition_Property.VideoBitDepth, tenBit.ApplyConditions!.Single().Property);
		Assert.Equal("4096", Condition(tenBit, ProfileCondition_Property.Width).Value);
	}

	[Fact]
	public void Av1WithoutGpuDecoding_HasNoLimitOfItsOwn()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(video: [.. InBoxVideo, WmpSubtypes.Av1]));

		var av1 = CodecProfile(profile, "av1");
		Assert.DoesNotContain(av1.Conditions!, c => c.Property == ProfileCondition_Property.Width);
		Assert.Contains("av1", DirectPlayVideoCodecs(profile, "mkv"));
	}

	[Fact]
	public void MissingDecoders_AreNotDirectPlayed()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(audio: [WmpSubtypes.Aac, WmpSubtypes.Mp3]));

		var mkv = profile.DirectPlayProfiles!.Single(p => p.Container == "mkv");
		Assert.DoesNotContain("hevc", mkv.VideoCodec!.Split(','));
		Assert.DoesNotContain("av1", mkv.VideoCodec!.Split(','));
		Assert.DoesNotContain("ac3", mkv.AudioCodec!.Split(','));
		Assert.DoesNotContain("truehd", mkv.AudioCodec!.Split(','));
		Assert.DoesNotContain(profile.DirectPlayProfiles!, p => p.Container == "flac");
		Assert.DoesNotContain(profile.DirectPlayProfiles!, p => p.Container == "webm");
	}

	[Fact]
	public void SdrDisplay_GetsHdrVideoToneMappedByTheServer()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(video: [.. InBoxVideo, WmpSubtypes.Hevc]));

		var rangeTypes = Condition(CodecProfile(profile, "hevc"), ProfileCondition_Property.VideoRangeType).Value!.Split('|');
		Assert.Equal(new[] { "SDR", "DOVIWithSDR" }, rangeTypes);
	}

	[Fact]
	public void HdrDisplay_GetsHdrVideoDirectPlayed()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(video: [.. InBoxVideo, WmpSubtypes.Hevc, WmpSubtypes.Vp9], hdr: true));

		var hevc = Condition(CodecProfile(profile, "hevc"), ProfileCondition_Property.VideoRangeType).Value!.Split('|');
		Assert.Contains("HDR10", hevc);
		Assert.Contains("HLG", hevc);
		Assert.Contains("DOVIWithHDR10", hevc);
		Assert.DoesNotContain("DOVI", hevc);

		// 10-bit VP9 is not known to decode without the GPU
		var vp9 = Condition(CodecProfile(profile, "vp9"), ProfileCondition_Property.VideoRangeType).Value!.Split('|');
		Assert.Equal(new[] { "SDR" }, vp9);
	}

	[Fact]
	public void DolbyVisionRenderedByWindows_IsDirectPlayedEvenToAnSdrDisplay()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(video: [.. InBoxVideo, WmpSubtypes.Hevc], dolbyVision: true));

		var hevc = Condition(CodecProfile(profile, "hevc"), ProfileCondition_Property.VideoRangeType).Value!.Split('|');
		Assert.Contains("DOVI", hevc);
		Assert.Contains("DOVIWithHDR10", hevc);
		Assert.DoesNotContain("HDR10", hevc);
		Assert.Equal(hevc.Length, hevc.Distinct().Count());
	}

	[Fact]
	public void DirectPlay_HasNoBitrateLimit()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities());

		Assert.Equal(int.MaxValue, profile.MaxStreamingBitrate);
		Assert.Equal(int.MaxValue, profile.MaxStaticBitrate);
		Assert.DoesNotContain(profile.CodecProfiles!.SelectMany(p => p.Conditions ?? []), c => c.Property == ProfileCondition_Property.VideoBitrate);
	}

	[Fact]
	public void ContainersWithoutAReader_AreNotDirectPlayed()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities(handlers: [".mp4", "video/x-matroska", ".mp3", ".wav"]));

		var containers = profile.DirectPlayProfiles!.SelectMany(p => p.Container!.Split(',')).ToHashSet();
		Assert.Contains("mkv", containers);
		Assert.Contains("mp4", containers);
		Assert.DoesNotContain("m4v", containers);
		Assert.DoesNotContain("avi", containers);
		Assert.DoesNotContain("ts", containers);

		// HLS is read by the adaptive streaming source, not a byte stream handler, and transcoding goes through it
		Assert.Contains("hls", containers);
		Assert.Contains(profile.TranscodingProfiles!, t => t.Protocol == TranscodingProfile_Protocol.Hls && t.Type == TranscodingProfile_Type.Video);
		Assert.DoesNotContain(profile.TranscodingProfiles!, t => t.Container == "aac");
	}

	[Fact]
	public void ContainersAreKept_WhenTheReadersCannotBeRead()
	{
		var profile = WmpDeviceProfileManager.Build(Capabilities());

		Assert.Contains(profile.DirectPlayProfiles!, p => p.Container == "avi");
	}

	[Theory]
	[InlineData(null, null, null)]
	[InlineData(3840, null, 3840)]
	[InlineData(null, 4096, 4096)]
	[InlineData(1920, 4096, 4096)]
	[InlineData(8192, 4096, 8192)]
	public void MaxVideoWidth_IsTheLargerOfHardwareAndSoftware(int? hardware, int? software, int? expected)
	{
		VideoSize? size = hardware is { } width ? new(width, width * 9 / 16) : null;
		Assert.Equal(expected, WmpDeviceProfileManager.MaxVideoWidth(size, software));
	}

	/// <summary>
	/// Reads the capabilities of the machine that runs the tests. A CI runner has no GPU and maybe no audio device, the probe must
	/// still return; what it found is written to the test output.
	/// </summary>
	[Fact]
	public async Task Probe_ReadsTheMachine()
	{
		var capabilities = await WmpCapabilityProbe.ProbeAsync(0);

		output.WriteLine($"Video decoders: {string.Join(", ", capabilities.VideoDecoders)}");
		output.WriteLine($"Audio decoders: {string.Join(", ", capabilities.AudioDecoders)}");
		output.WriteLine($"Hardware decoders: {string.Join(", ", capabilities.HardwareDecoders.Select(x => $"{x.Key} {x.Value.Width}x{x.Value.Height}"))}");
		output.WriteLine($"Byte stream handlers: {(capabilities.ByteStreamHandlers is { } handlers ? string.Join(", ", handlers.Order()) : "not read")}");
		output.WriteLine($"HDR display: {capabilities.IsHdrDisplay}, Dolby Vision: {capabilities.CanPlayDolbyVision}, audio channels: {capabilities.AudioChannelCount}");

		var profile = WmpDeviceProfileManager.Build(capabilities);
		Assert.NotEmpty(profile.DirectPlayProfiles!);
	}

	private static WmpCapabilities Capabilities(
		Guid[]? video = null,
		Guid[]? audio = null,
		Dictionary<HardwareDecoder, VideoSize>? hardware = null,
		bool hdr = false,
		bool dolbyVision = false,
		string[]? handlers = null)
	{
		return new WmpCapabilities
		{
			VideoDecoders = new HashSet<Guid>(video ?? InBoxVideo),
			AudioDecoders = new HashSet<Guid>(audio ?? InBoxAudio),
			HardwareDecoders = hardware ?? [],
			IsHdrDisplay = hdr,
			CanPlayDolbyVision = dolbyVision,
			ByteStreamHandlers = handlers is null ? null : new HashSet<string>(handlers, StringComparer.OrdinalIgnoreCase),
			AudioChannelCount = 2,
		};
	}

	private static CodecProfile CodecProfile(DeviceProfile profile, string codec)
	{
		return profile.CodecProfiles!.Single(p => p.Type == CodecProfile_Type.Video && p.Codec == codec && p.ApplyConditions is not { Count: > 0 });
	}

	private static ProfileCondition Condition(CodecProfile codecProfile, ProfileCondition_Property property)
	{
		return codecProfile.Conditions!.Single(c => c.Property == property);
	}

	private static string[] DirectPlayVideoCodecs(DeviceProfile profile, string container)
	{
		return profile.DirectPlayProfiles!.Single(p => p.Container == container).VideoCodec!.Split(',');
	}
}
