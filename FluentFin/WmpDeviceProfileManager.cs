using Jellyfin.Sdk.Generated.Models;

namespace FluentFin;

public sealed class WmpDeviceProfileManager
{
	public static DeviceProfile Profile { get; private set; } = null!;

	/// <summary>
	/// The widest video that is direct played, DCI 8K. Each codec is limited further to what this machine decodes, see
	/// <see cref="MaxVideoWidth"/>.
	/// </summary>
	internal const int MaxVideoWidth8K = 8192;

	/// <summary>
	/// The largest picture of Microsoft's in-box H.264 and HEVC decoders, in hardware or software, documented on
	/// https://learn.microsoft.com/windows/win32/medfound/h-264-video-decoder and
	/// https://learn.microsoft.com/windows/win32/medfound/h-265---hevc-video-decoder.
	/// </summary>
	internal const int MediaFoundationDecoderMaxWidth = 4096;

	public static async Task InitializeAsync()
	{
		Profile = Build(await WmpCapabilityProbe.ProbeAsync());
	}

	/// <summary>
	/// The widest video of a codec that this machine decodes: the larger of what its GPU decodes (probed) and what the software
	/// decoder is documented to decode. When neither is known (a software decoder from the Store that documents no limit) only the
	/// 8K limit of all video applies.
	/// </summary>
	internal static int? MaxVideoWidth(VideoSize? hardware, int? documentedSoftware)
	{
		int? width = (hardware?.Width, documentedSoftware) switch
		{
			(null, null) => null,
			({ } h, null) => h,
			(null, { } s) => s,
			({ } h, { } s) => Math.Max(h, s),
		};

		return width is { } w ? Math.Min(w, MaxVideoWidth8K) : null;
	}

	// This logic is adapted from the web client's browserDeviceProfile.js, the canPlayType() checks of the browser are replaced by
	// what Media Foundation, which the Windows media player plays with, has on this machine.
	internal static DeviceProfile Build(WmpCapabilities capabilities)
	{
		const int maxBitrate = 120_000_000;
		uint audioChannelCount = capabilities.AudioChannelCount;

		bool canPlayVorbis = capabilities.CanDecodeAudio(WmpSubtypes.Vorbis);
		List<string> webmAudioCodecs = [];
		if (canPlayVorbis)
		{
			webmAudioCodecs.Add("vorbis");
		}

		// Media Foundation reads Matroska since the first Windows 10, older than the oldest Windows the app runs on (1809)
		bool canPlayMkv = true;

		DeviceProfile profile = new()
		{
			MaxStreamingBitrate = maxBitrate,
			MaxStaticBitrate = 100_000_000,
			MusicStreamingTranscodingBitrate = Math.Min(maxBitrate, 384_000),
			DirectPlayProfiles = [],
			TranscodingProfiles = [],
			ContainerProfiles = [],
			CodecProfiles = [],
			SubtitleProfiles = [],
		};

		List<string> videoAudioCodecs = [];
		List<string> hlsInTsVideoAudioCodecs = [];
		List<string> hlsInFmp4VideoAudioCodecs = [];

		// The HLS streams are read by the same Media Foundation decoders as the files, so an audio codec plays in HLS when it has a
		// decoder.
		bool canPlayAacVideoAudio = capabilities.CanDecodeAudio(WmpSubtypes.Aac);
		bool supportsMp3VideoAudio = capabilities.CanDecodeAudio(WmpSubtypes.Mp3);
		bool canPlayMp3VideoAudioInHls = supportsMp3VideoAudio;
		bool canPlayAc3VideoAudio = capabilities.CanDecodeAudio(WmpSubtypes.Ac3);
		bool canPlayEac3VideoAudio = capabilities.CanDecodeAudio(WmpSubtypes.Eac3);
		bool canPlayAc3VideoAudioInHls = canPlayAc3VideoAudio;

		// Transcoding codec is the first in hlsVideoAudioCodecs.
		// Prefer AAC, MP3 to other codecs when audio transcoding.
		if (canPlayAacVideoAudio)
		{
			videoAudioCodecs.Add("aac");
			hlsInTsVideoAudioCodecs.Add("aac");
			hlsInFmp4VideoAudioCodecs.Add("aac");
		}

		if (supportsMp3VideoAudio)
		{
			videoAudioCodecs.Add("mp3");
			hlsInTsVideoAudioCodecs.Add("mp3");
		}

		if (canPlayMp3VideoAudioInHls)
		{
			hlsInFmp4VideoAudioCodecs.Add("mp3");
		}

		// For AC3/EAC3 remuxing.
		// Do not use AC3 for audio transcoding unless AAC and MP3 are not supported.
		if (canPlayAc3VideoAudio)
		{
			videoAudioCodecs.Add("ac3");

			if (canPlayEac3VideoAudio)
			{
				videoAudioCodecs.Add("eac3");
			}

			if (canPlayAc3VideoAudioInHls)
			{
				hlsInTsVideoAudioCodecs.Add("ac3");
				hlsInFmp4VideoAudioCodecs.Add("ac3");

				if (canPlayEac3VideoAudio)
				{
					hlsInTsVideoAudioCodecs.Add("eac3");
					hlsInFmp4VideoAudioCodecs.Add("eac3");
				}
			}
		}

		bool supportsMp2VideoAudio = capabilities.CanDecodeAudio(WmpSubtypes.Mpeg);
		if (supportsMp2VideoAudio)
		{
			videoAudioCodecs.Add("mp2");
			hlsInTsVideoAudioCodecs.Add("mp2");
			hlsInFmp4VideoAudioCodecs.Add("mp2");
		}

		// TODO: Check user setting: enableDts
		bool supportsDts = capabilities.CanDecodeAudio(WmpSubtypes.Dts);
		if (supportsDts)
		{
			videoAudioCodecs.Add("dca");
			videoAudioCodecs.Add("dts");
		}

		// TrueHD is not listed: Media Foundation has no media subtype, and so no decoder, for it.

		bool canPlayOpus = capabilities.CanDecodeAudio(WmpSubtypes.Opus);
		if (canPlayOpus)
		{
			videoAudioCodecs.Add("opus");
			webmAudioCodecs.Add("opus");
			hlsInFmp4VideoAudioCodecs.Add("opus");
		}

		bool canPlayFlac = capabilities.CanDecodeAudio(WmpSubtypes.Flac);
		if (canPlayFlac)
		{
			videoAudioCodecs.Add("flac");
			hlsInFmp4VideoAudioCodecs.Add("flac");
		}

		bool canPlayAlac = capabilities.CanDecodeAudio(WmpSubtypes.Alac);
		if (canPlayAlac)
		{
			videoAudioCodecs.Add("alac");
			hlsInFmp4VideoAudioCodecs.Add("alac");
		}

		List<string> mp4VideoCodecs = [];
		List<string> webmVideoCodecs = [];
		List<string> hlsInTsVideoCodecs = [];
		List<string> hlsInFmp4VideoCodecs = [];

		bool canPlayAv1 = capabilities.CanDecodeVideo(WmpSubtypes.Av1);
		if (canPlayAv1)
		{
			hlsInFmp4VideoCodecs.Add("av1");
		}

		bool canPlayHevc = capabilities.CanDecodeVideo(WmpSubtypes.Hevc);
		if (canPlayHevc)
		{
			hlsInFmp4VideoCodecs.Add("hevc");
		}

		bool canPlayH264 = capabilities.CanDecodeVideo(WmpSubtypes.H264);
		if (canPlayH264)
		{
			mp4VideoCodecs.Add("h264");
			hlsInTsVideoCodecs.Add("h264");
			hlsInFmp4VideoCodecs.Add("h264");
		}

		if (canPlayHevc)
		{
			mp4VideoCodecs.Add("hevc");
		}

		bool supportsMpeg2Video = capabilities.CanDecodeVideo(WmpSubtypes.Mpeg2);
		if (supportsMpeg2Video)
		{
			mp4VideoCodecs.Add("mpeg2video");
		}

		bool supportsVc1 = capabilities.CanDecodeVideo(WmpSubtypes.Vc1);
		if (supportsVc1)
		{
			mp4VideoCodecs.Add("vc1");
		}

		bool canPlayVp8 = capabilities.CanDecodeVideo(WmpSubtypes.Vp8);
		if (canPlayVp8)
		{
			webmVideoCodecs.Add("vp8");
		}

		bool canPlayVp9 = capabilities.CanDecodeVideo(WmpSubtypes.Vp9);
		if (canPlayVp9)
		{
			mp4VideoCodecs.Add("vp9");
			hlsInFmp4VideoCodecs.Add("vp9");
			webmVideoCodecs.Add("vp9");
		}

		if (canPlayAv1)
		{
			mp4VideoCodecs.Add("av1");
			webmVideoCodecs.Add("av1");
		}

		if (canPlayVorbis)
		{
			videoAudioCodecs.Add("vorbis");
		}

		if (webmVideoCodecs.Count > 0)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "webm",
					Type = DirectPlayProfile_Type.Video,
					VideoCodec = string.Join(',', webmVideoCodecs),
					AudioCodec = string.Join(',', webmAudioCodecs),
				});
		}

		if (mp4VideoCodecs.Count > 0)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "mp4,m4v",
					Type = DirectPlayProfile_Type.Video,
					VideoCodec = string.Join(',', mp4VideoCodecs),
					AudioCodec = string.Join(',', videoAudioCodecs),
				});
		}

		if (canPlayMkv && mp4VideoCodecs.Count > 0)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "mkv",
					Type = DirectPlayProfile_Type.Video,
					VideoCodec = string.Join(',', mp4VideoCodecs),
					AudioCodec = string.Join(',', videoAudioCodecs),
				});
		}

		// the video codecs of the MPEG transport streams (m2ts and ts)
		List<string> transportStreamVideoCodecs = [];
		if (canPlayH264)
		{
			transportStreamVideoCodecs.Add("h264");
		}

		if (supportsVc1)
		{
			transportStreamVideoCodecs.Add("vc1");
		}

		if (supportsMpeg2Video)
		{
			transportStreamVideoCodecs.Add("mpeg2video");
		}

		if (transportStreamVideoCodecs.Count > 0)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "m2ts",
					Type = DirectPlayProfile_Type.Video,
					VideoCodec = string.Join(',', transportStreamVideoCodecs),
					AudioCodec = string.Join(',', videoAudioCodecs),
				});
		}

		profile.DirectPlayProfiles.Add(
			new DirectPlayProfile
			{
				Container = "wmv",
				Type = DirectPlayProfile_Type.Video,
			});

		if (transportStreamVideoCodecs.Count > 0)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "ts,mpegts",
					Type = DirectPlayProfile_Type.Video,
					VideoCodec = string.Join(',', transportStreamVideoCodecs),
					AudioCodec = string.Join(',', videoAudioCodecs),
				});
		}

		profile.DirectPlayProfiles.Add(
			new DirectPlayProfile
			{
				Container = "asf",
				Type = DirectPlayProfile_Type.Video
			});
		profile.DirectPlayProfiles.Add(
			new DirectPlayProfile
			{
				Container = "avi",
				Type = DirectPlayProfile_Type.Video,
				AudioCodec = string.Join(',', videoAudioCodecs),
			});
		profile.DirectPlayProfiles.Add(
			new DirectPlayProfile
			{
				Container = "mpg",
				Type = DirectPlayProfile_Type.Video,
				AudioCodec = string.Join(',', videoAudioCodecs),
			});
		profile.DirectPlayProfiles.Add(
			new DirectPlayProfile
			{
				Container = "mpeg",
				Type = DirectPlayProfile_Type.Video,
				AudioCodec = string.Join(',', videoAudioCodecs),
			});

		if (canPlayH264)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "mov",
					Type = DirectPlayProfile_Type.Video,
					VideoCodec = "h264",
					AudioCodec = string.Join(',', videoAudioCodecs),
				});
		}

		if (canPlayOpus)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "webm",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = "opus",
				});
		}

		bool canPlayMp3 = supportsMp3VideoAudio;

		if (supportsMp2VideoAudio)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "mp2",
					Type = DirectPlayProfile_Type.Audio,
				});
		}

		bool canPlayAac = canPlayAacVideoAudio;
		if (canPlayAac)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "aac",
					Type = DirectPlayProfile_Type.Audio,
				});
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "m4a",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = "aac",
				});
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "m4b",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = "aac",
				});
		}

		if (canPlayFlac)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "flac",
					Type = DirectPlayProfile_Type.Audio,
				});
		}

		if (canPlayAlac)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "alac",
					Type = DirectPlayProfile_Type.Audio,
				});
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "m4a",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = "alac",
				});
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "m4b",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = "alac",
				});
		}

		if (webmAudioCodecs.Count > 0)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "webma",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = string.Join(',', webmAudioCodecs),
				});
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "webm",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = string.Join(',', webmAudioCodecs),
				});
		}

		if (capabilities.CanDecodeAudio(WmpSubtypes.WmaV8)
			|| capabilities.CanDecodeAudio(WmpSubtypes.WmaV9)
			|| capabilities.CanDecodeAudio(WmpSubtypes.WmaLossless))
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "wma",
					Type = DirectPlayProfile_Type.Audio,
				});
		}

		// PCM needs no decoder
		profile.DirectPlayProfiles.Add(
			new DirectPlayProfile
			{
				Container = "wav",
				Type = DirectPlayProfile_Type.Audio,
			});

		// Ogg is read by the Web Media Extensions, the package that also brings the Vorbis decoder
		List<string> oggAudioCodecs = [.. webmAudioCodecs];
		if (canPlayFlac)
		{
			oggAudioCodecs.Add("flac");
		}

		if (canPlayVorbis)
		{
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "ogg",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = string.Join(',', oggAudioCodecs),
				});
			profile.DirectPlayProfiles.Add(
				new DirectPlayProfile
				{
					Container = "oga",
					Type = DirectPlayProfile_Type.Audio,
					AudioCodec = string.Join(',', oggAudioCodecs),
				});
		}

		bool enableFmp4Hls = true; // TODO: check user setting: preferFmp4HlsContainer

		profile.TranscodingProfiles.Add(
			new TranscodingProfile
			{
				Container = enableFmp4Hls ? "mp4" : "ts",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "aac",
				Context = TranscodingProfile_Context.Streaming,
				Protocol = TranscodingProfile_Protocol.Hls,
				MaxAudioChannels = audioChannelCount.ToString(),
				MinSegments = 1,
				EnableAudioVbrEncoding = false // TODO: ??? !appSettings.disableVbrAudio()
			});

		// For streaming, prioritize opus transcoding after mp3/aac. It is too problematic with random failures
		// But for static (offline sync), it will be just fine.
		// Prioritize aac higher because the encoder can accept more channels than mp3
		if (canPlayAac)
		{
			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "aac",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "aac",
				Context = TranscodingProfile_Context.Streaming,
				Protocol = TranscodingProfile_Protocol.Http,
				MaxAudioChannels = audioChannelCount.ToString(),
			});
		}

		if (canPlayMp3)
		{
			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "mp3",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "mp3",
				Context = TranscodingProfile_Context.Streaming,
				Protocol = TranscodingProfile_Protocol.Http,
				MaxAudioChannels = audioChannelCount.ToString(),
			});
		}

		if (canPlayOpus)
		{
			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "opus",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "opus",
				Context = TranscodingProfile_Context.Streaming,
				Protocol = TranscodingProfile_Protocol.Http,
				MaxAudioChannels = audioChannelCount.ToString(),
			});
		}

		profile.TranscodingProfiles.Add(new TranscodingProfile
		{
			Container = "wav",
			Type = TranscodingProfile_Type.Audio,
			AudioCodec = "wav",
			Context = TranscodingProfile_Context.Streaming,
			Protocol = TranscodingProfile_Protocol.Http,
			MaxAudioChannels = audioChannelCount.ToString(),
		});

		if (canPlayOpus)
		{
			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "opus",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "opus",
				Context = TranscodingProfile_Context.Static,
				Protocol = TranscodingProfile_Protocol.Http,
				MaxAudioChannels = audioChannelCount.ToString(),
			});
		}

		if (canPlayMp3)
		{
			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "mp3",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "mp3",
				Context = TranscodingProfile_Context.Static,
				Protocol = TranscodingProfile_Protocol.Http,
				MaxAudioChannels = audioChannelCount.ToString(),
			});
		}

		if (canPlayAac)
		{
			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "aac",
				Type = TranscodingProfile_Type.Audio,
				AudioCodec = "aac",
				Context = TranscodingProfile_Context.Static,
				Protocol = TranscodingProfile_Protocol.Http,
				MaxAudioChannels = audioChannelCount.ToString(),
			});
		}

		profile.TranscodingProfiles.Add(new TranscodingProfile
		{
			Container = "wav",
			Type = TranscodingProfile_Type.Audio,
			AudioCodec = "wav",
			Context = TranscodingProfile_Context.Static,
			Protocol = TranscodingProfile_Protocol.Http,
			MaxAudioChannels = audioChannelCount.ToString(),
		});

		if (hlsInFmp4VideoCodecs.Count > 0 && hlsInFmp4VideoAudioCodecs.Count > 0 && enableFmp4Hls)
		{
			// HACK: Since there is no filter for TS/MP4 in the API, specify HLS support in general and rely on retry after DirectPlay error
			// FIXME: Need support for {Container = "mp4", Protocol: "hls"} or {Container = "hls", SubContainer = "mp4"}
			profile.DirectPlayProfiles.Add(new DirectPlayProfile
			{
				Container = "hls",
				Type = DirectPlayProfile_Type.Video,
				VideoCodec = string.Join(',', hlsInFmp4VideoCodecs),
				AudioCodec = string.Join(',', hlsInFmp4VideoAudioCodecs)
			});

			profile.TranscodingProfiles.Add(new TranscodingProfile
			{
				Container = "mp4",
				Type = TranscodingProfile_Type.Video,
				AudioCodec = string.Join(',', hlsInFmp4VideoAudioCodecs),
				VideoCodec = string.Join(',', hlsInFmp4VideoCodecs),
				Context = TranscodingProfile_Context.Streaming,
				Protocol = TranscodingProfile_Protocol.Hls,
				MaxAudioChannels = audioChannelCount.ToString(),
				MinSegments = 1,
			});
		}

		List<ProfileCondition> aacCodecProfileConditions = [];

		// Media Foundation has one AAC decoder, Microsoft's, which decodes AAC-LC and HE-AAC v1 and v2, up to 6 channels and 48 kHz:
		// https://learn.microsoft.com/windows/win32/medfound/aac-decoder
		List<ProfileCondition> aacDecoderConditions =
		[
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.AudioChannels,
				Value = "6",
				IsRequired = false,
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.AudioSampleRate,
				Value = "48000",
				IsRequired = false,
			},
		];
		aacCodecProfileConditions.AddRange(aacDecoderConditions);
		profile.CodecProfiles.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Audio,
				Codec = "aac",
				Conditions = aacDecoderConditions,
			});

		bool supportsSecondaryAudio = false; // TODO
		if (!supportsSecondaryAudio)
		{
			aacCodecProfileConditions.Add(
				new ProfileCondition
				{
					Condition = ProfileCondition_Condition.Equals,
					Property = ProfileCondition_Property.IsSecondaryAudio,
					Value = "false",
					IsRequired = false
				});
		}

		if (aacCodecProfileConditions.Count > 0)
		{
			profile.CodecProfiles.Add(
				new CodecProfile
				{
					Type = CodecProfile_Type.VideoAudio,
					Codec = "aac",
					Conditions = aacCodecProfileConditions
				});
		}

		List<ProfileCondition> globalAudioCodecProfileConditions = [];
		List<ProfileCondition> globalVideoAudioCodecProfileConditions = [];

		// TODO: Check user settings allowedAudioChannels
		/*

        if (parseInt(userSettings.allowedAudioChannels(), 10) > 0) {
            globalAudioCodecProfileConditions.Add({
                Condition = ProfileCondition_Condition.LessThanEqual',
                Property = ProfileCondition_Property.AudioChannels',
                Value = audioChannelCount,
                IsRequired = false
            });

            globalVideoAudioCodecProfileConditions.Add({
                Condition = ProfileCondition_Condition.LessThanEqual',
                Property = ProfileCondition_Property.AudioChannels',
                Value = audioChannelCount,
                IsRequired = false
            });
        }
        */

		if (!supportsSecondaryAudio)
		{
			globalVideoAudioCodecProfileConditions.Add(
				new ProfileCondition
				{
					Condition = ProfileCondition_Condition.Equals,
					Property = ProfileCondition_Property.IsSecondaryAudio,
					Value = "false",
					IsRequired = false
				});
		}

		if (globalAudioCodecProfileConditions.Count > 0)
		{
			profile.CodecProfiles.Add(
				new CodecProfile
				{
					Type = CodecProfile_Type.Audio,
					Conditions = globalAudioCodecProfileConditions
				});
		}

		if (globalVideoAudioCodecProfileConditions.Count > 0)
		{
			profile.CodecProfiles.Add(
				new CodecProfile
				{
					Type = CodecProfile_Type.VideoAudio,
					Conditions = globalVideoAudioCodecProfileConditions
				});
		}

		// Media Foundation has one H.264 decoder, Microsoft's, which decodes Baseline, Main and High (not High 10) up to level 5.1:
		// https://learn.microsoft.com/windows/win32/medfound/h-264-video-decoder
		const int maxH264Level = 51;
		const string h264Profiles = "high|main|baseline|constrained baseline";
		int? h264MaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.H264), MediaFoundationDecoderMaxWidth);

		// Microsoft's HEVC decoder (HEVC Video Extensions) decodes Main and Main 10:
		// https://learn.microsoft.com/windows/win32/medfound/h-265---hevc-video-decoder
		const string hevcProfiles = "main|main 10";
		int? hevcMaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.Hevc), MediaFoundationDecoderMaxWidth);
		int? hevc10BitMaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.Hevc10Bit), MediaFoundationDecoderMaxWidth);
		int maxHevcLevel = HevcLevel(Math.Max(hevcMaxWidth ?? MaxVideoWidth8K, hevc10BitMaxWidth ?? MaxVideoWidth8K));

		// The AV1 Video Extension and the VP9 Video Extensions document no limit of their software decoders, so these codecs are only
		// limited by what the GPU decodes, when it decodes them.
		const string av1Profiles = "main"; // av1 main covers 4:2:0 8 & 10 bits
		int? av1MaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.Av1), null);
		int? av110BitMaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.Av1TenBit), null);
		int maxAv1Level = Av1Level(Math.Max(av1MaxWidth ?? MaxVideoWidth8K, av110BitMaxWidth ?? MaxVideoWidth8K));

		int? vp9MaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.Vp9), null);
		int? vp9Profile2MaxWidth = MaxVideoWidth(capabilities.HardwareMaxSize(HardwareDecoder.Vp9Profile2), null);

		string h264VideoRangeTypes = "SDR";

		// Dolby Vision with an SDR, HDR10 or HLG base layer plays as its base layer when Dolby Vision is not rendered, the decoder
		// skips the Dolby Vision NAL units.
		string hevcVideoRangeTypes = "SDR|DOVIWithSDR";
		string av1VideoRangeTypes = "SDR|DOVIWithSDR";
		string vp9VideoRangeTypes = "SDR";

		// HDR video is direct played only to a display that is in HDR mode, otherwise the server tone maps it to SDR. HLG is kept with
		// HDR10 so that both are tone mapped the same way.
		if (capabilities.IsHdrDisplay)
		{
			// HEVC Main 10 and AV1 Main decode 10-bit video
			hevcVideoRangeTypes += "|HDR10|HDR10Plus|HLG|DOVIWithHDR10|DOVIWithHDR10Plus|DOVIWithHLG";
			av1VideoRangeTypes += "|HDR10|HDR10Plus|HLG|DOVIWithHDR10|DOVIWithHDR10Plus|DOVIWithHLG";

			// 10-bit VP9 is profile 2, which is only known to decode where the GPU decodes it
			if (capabilities.HardwareMaxSize(HardwareDecoder.Vp9Profile2) is not null)
			{
				vp9VideoRangeTypes += "|HDR10|HDR10Plus|HLG";
			}
		}

		// Dolby Vision itself, including profile 5 that has no compatible base layer, when Media Foundation renders it (Dolby Vision
		// Extensions on a Dolby Vision capable device); its display management maps it to SDR displays as well. Only HEVC is queried,
		// there is no query for Dolby Vision in AV1 (profile 10).
		if (capabilities.CanPlayDolbyVision)
		{
			foreach (string rangeType in new[] { "DOVI", "DOVIWithHDR10", "DOVIWithHDR10Plus", "DOVIWithHLG" })
			{
				if (!hevcVideoRangeTypes.Split('|').Contains(rangeType))
				{
					hevcVideoRangeTypes += "|" + rangeType;
				}
			}
		}

		List<ProfileCondition> h264CodecProfileConditions =
		[
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.NotEquals,
				Property = ProfileCondition_Property.IsAnamorphic,
				Value = "true",
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoProfile,
				Value = h264Profiles,
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoRangeType,
				Value = h264VideoRangeTypes,
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.VideoLevel,
				Value = maxH264Level.ToString(),
				IsRequired = false
			}
		];

		List<ProfileCondition> hevcCodecProfileConditions =
		[
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.NotEquals,
				Property = ProfileCondition_Property.IsAnamorphic,
				Value = "true",
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoProfile,
				Value = hevcProfiles,
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoRangeType,
				Value = hevcVideoRangeTypes,
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.VideoLevel,
				Value = maxHevcLevel.ToString(),
				IsRequired = false
			}
		];

		List<ProfileCondition> vp9CodecProfileConditions =
		[
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoRangeType,
				Value = vp9VideoRangeTypes,
				IsRequired = false
			}
		];

		List<ProfileCondition> av1CodecProfileConditions =
		[
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.NotEquals,
				Property = ProfileCondition_Property.IsAnamorphic,
				Value = "true",
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoProfile,
				Value = av1Profiles,
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.EqualsAny,
				Property = ProfileCondition_Property.VideoRangeType,
				Value = av1VideoRangeTypes,
				IsRequired = false
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.VideoLevel,
				Value = maxAv1Level.ToString(),
				IsRequired = false
			}
		];

		AddMaxWidth(h264CodecProfileConditions, h264MaxWidth);
		AddMaxWidth(hevcCodecProfileConditions, hevcMaxWidth);
		AddMaxWidth(vp9CodecProfileConditions, vp9MaxWidth);
		AddMaxWidth(av1CodecProfileConditions, av1MaxWidth);

		profile.CodecProfiles.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Video,
				Codec = "h264",
				Conditions = h264CodecProfileConditions,
			});

		profile.CodecProfiles.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Video,
				Codec = "hevc",
				Conditions = hevcCodecProfileConditions,
			});

		profile.CodecProfiles.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Video,
				Codec = "vp9",
				Conditions = vp9CodecProfileConditions,
			});

		profile.CodecProfiles.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Video,
				Codec = "av1",
				Conditions = av1CodecProfileConditions,
			});

		// A GPU may decode 10-bit video only up to a smaller size than 8-bit video, or not at all
		AddTenBitMaxWidth(profile, "hevc", hevc10BitMaxWidth, hevcMaxWidth);
		AddTenBitMaxWidth(profile, "vp9", vp9Profile2MaxWidth, vp9MaxWidth);
		AddTenBitMaxWidth(profile, "av1", av110BitMaxWidth, av1MaxWidth);

		List<ProfileCondition> globalVideoConditions =
		[
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.VideoBitrate,
				Value = maxBitrate.ToString(),
			},
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.Width,
				Value = MaxVideoWidth8K.ToString(),
				IsRequired = false,
			},
		];

		profile.CodecProfiles.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Video,
				Conditions = globalVideoConditions,
			});

		// Subtitle profiles
		// External vtt or burn in
		string subtitleBurninSetting = ""; // TODO: appSettings.get("subtitleburnin");
		if (subtitleBurninSetting != "all")
		{
			profile.SubtitleProfiles.Add(
				new SubtitleProfile
				{
					Format = "subrip",
					Method = SubtitleProfile_Method.External,
				});

			bool supportsTextTracks = false; // TODO: Check
			if (supportsTextTracks)
			{
				profile.SubtitleProfiles.Add(
					new SubtitleProfile
					{
						Format = "vtt",
						Method = SubtitleProfile_Method.External,
					});
			}

			bool enableSsaRender = false; // TODO: Check settings
			if (!enableSsaRender && subtitleBurninSetting != "allcomplexformats")
			{
				profile.SubtitleProfiles.Add(
					new SubtitleProfile
					{
						Format = "ass",
						Method = SubtitleProfile_Method.External
					});
				profile.SubtitleProfiles.Add(
					new SubtitleProfile
					{
						Format = "ssa",
						Method = SubtitleProfile_Method.External
					});
			}

			// TODO: Can we overlay an image on top?
			/*
            if (options.enablePgsRender !== false
                && appSettings.get("subtitlerenderpgs") == "true"
                && subtitleBurninSetting != "allcomplexformats"
                && subtitleBurninSetting != "onlyimageformats")
            {
                profile.SubtitleProfiles.Add(
                    new SubtitleProfile
                    {
                        Format = "pgssub",
                        Method = SubtitleProfile_Method.External
                    });
            }
            */
		}

		return profile;
	}

	/// <summary>
	/// The highest HEVC level whose largest picture (ITU-T H.265 Annex A) a decoder of that width can take: level 5.2 up to 4K, 6.2 up
	/// to 8K. The level is written the way Jellyfin compares it, 30 times the level.
	/// </summary>
	internal static int HevcLevel(int maxWidth) => maxWidth > MediaFoundationDecoderMaxWidth ? 186 : 156;

	/// <summary>
	/// The highest AV1 level (seq_level_idx) whose largest picture (AV1 specification Annex A) a decoder of that width can take: 5.3 up
	/// to 4K, 6.3 up to 8K.
	/// </summary>
	internal static int Av1Level(int maxWidth) => maxWidth > MediaFoundationDecoderMaxWidth ? 19 : 15;

	private static void AddMaxWidth(List<ProfileCondition> conditions, int? maxWidth)
	{
		if (maxWidth is not { } width)
		{
			return;
		}

		conditions.Add(
			new ProfileCondition
			{
				Condition = ProfileCondition_Condition.LessThanEqual,
				Property = ProfileCondition_Property.Width,
				Value = width.ToString(),
				IsRequired = false
			});
	}

	private static void AddTenBitMaxWidth(DeviceProfile profile, string codec, int? tenBitMaxWidth, int? maxWidth)
	{
		if (tenBitMaxWidth is not { } width || width >= (maxWidth ?? MaxVideoWidth8K))
		{
			return;
		}

		profile.CodecProfiles!.Add(
			new CodecProfile
			{
				Type = CodecProfile_Type.Video,
				Codec = codec,
				ApplyConditions =
				[
					new ProfileCondition
					{
						Condition = ProfileCondition_Condition.GreaterThanEqual,
						Property = ProfileCondition_Property.VideoBitDepth,
						Value = "10",
						IsRequired = false
					},
				],
				Conditions =
				[
					new ProfileCondition
					{
						Condition = ProfileCondition_Condition.LessThanEqual,
						Property = ProfileCondition_Property.Width,
						Value = width.ToString(),
						IsRequired = false
					},
				],
			});
	}
}
