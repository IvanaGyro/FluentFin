using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace FluentFin;

/// <summary>
/// The media subtypes (MF_MT_SUBTYPE) of the decoders that the Windows media player (Media Foundation) can use.
/// </summary>
public static class WmpSubtypes
{
	public static readonly Guid H264 = Guid.Parse(CodecSubtypes.VideoFormatH264);
	public static readonly Guid Hevc = Guid.Parse(CodecSubtypes.VideoFormatHevc);
	public static readonly Guid Mpeg2 = Guid.Parse(CodecSubtypes.VideoFormatMpeg2);
	public static readonly Guid Vc1 = Guid.Parse(CodecSubtypes.VideoFormatWvc1);
	public static readonly Guid Vp8 = Guid.Parse(CodecSubtypes.VideoFormatVP80);
	public static readonly Guid Vp9 = Guid.Parse(CodecSubtypes.VideoFormatVP90);

	// MFVideoFormat_AV1 of mfapi.h, the media subtype of the FourCC 'AV01'; CodecSubtypes has no AV1 entry
	public static readonly Guid Av1 = new("31305641-0000-0010-8000-00AA00389B71");

	public static readonly Guid Aac = Guid.Parse(CodecSubtypes.AudioFormatAac);
	public static readonly Guid Mp3 = Guid.Parse(CodecSubtypes.AudioFormatMP3);
	// MFAudioFormat_MPEG, MPEG-1 audio layer 1 and 2
	public static readonly Guid Mpeg = Guid.Parse(CodecSubtypes.AudioFormatMPeg);
	public static readonly Guid Ac3 = Guid.Parse(CodecSubtypes.AudioFormatDolbyAC3);
	public static readonly Guid Eac3 = Guid.Parse(CodecSubtypes.AudioFormatDolbyDDPlus);
	public static readonly Guid Dts = Guid.Parse(CodecSubtypes.AudioFormatDts);
	public static readonly Guid Opus = Guid.Parse(CodecSubtypes.AudioFormatOpus);
	public static readonly Guid Flac = Guid.Parse(CodecSubtypes.AudioFormatFlac);
	public static readonly Guid Alac = Guid.Parse(CodecSubtypes.AudioFormatAlac);
	public static readonly Guid WmaV8 = Guid.Parse(CodecSubtypes.AudioFormatWMAudioV8);
	public static readonly Guid WmaV9 = Guid.Parse(CodecSubtypes.AudioFormatWMAudioV9);
	public static readonly Guid WmaLossless = Guid.Parse(CodecSubtypes.AudioFormatWMAudioLossless);

	// MFAudioFormat_Vorbis of mfapi.h; CodecSubtypes has no Vorbis entry
	public static readonly Guid Vorbis = new("8D2FD10B-5841-4A6B-8905-588FEC1ADED9");

	public static readonly Guid[] Video = [H264, Hevc, Mpeg2, Vc1, Vp8, Vp9, Av1];
	public static readonly Guid[] Audio = [Aac, Mp3, Mpeg, Ac3, Eac3, Dts, Opus, Flac, Alac, WmaV8, WmaV9, WmaLossless, Vorbis];
}

/// <summary>
/// A hardware (DXVA) decoder of the GPU, the D3D11 decoder profile and the output format that it decodes to.
/// </summary>
public enum HardwareDecoder
{
	H264,
	Hevc,
	Hevc10Bit,
	Vp9,
	Vp9Profile2,
	Av1,
	Av1TenBit,
}

public readonly record struct VideoSize(int Width, int Height);

/// <summary>
/// What the media pipeline of the Windows media player (MediaPlayer, which plays with Media Foundation) can do on this machine.
/// </summary>
public sealed record WmpCapabilities
{
	/// <summary>Subtypes (<see cref="WmpSubtypes"/>) of the video decoders that are installed, in hardware or in software.</summary>
	public required IReadOnlySet<Guid> VideoDecoders { get; init; }

	/// <summary>Subtypes (<see cref="WmpSubtypes"/>) of the audio decoders that are installed.</summary>
	public required IReadOnlySet<Guid> AudioDecoders { get; init; }

	/// <summary>The largest picture each hardware decoder of the GPU decodes; a decoder that the GPU lacks is not listed.</summary>
	public required IReadOnlyDictionary<HardwareDecoder, VideoSize> HardwareDecoders { get; init; }

	/// <summary>Whether a display is in HDR (BT.2100 PQ) mode, so that HDR video is shown as HDR instead of washed out.</summary>
	public required bool IsHdrDisplay { get; init; }

	public required uint AudioChannelCount { get; init; }

	public bool CanDecodeVideo(Guid subtype) => VideoDecoders.Contains(subtype);

	public bool CanDecodeAudio(Guid subtype) => AudioDecoders.Contains(subtype);

	public VideoSize? HardwareMaxSize(HardwareDecoder decoder) => HardwareDecoders.TryGetValue(decoder, out var size) ? size : null;
}

/// <summary>
/// Reads <see cref="WmpCapabilities"/> from the system. Every probe fails soft: what cannot be read is reported as not supported,
/// so that the server transcodes instead of the player failing.
/// </summary>
public static class WmpCapabilityProbe
{
	// D3D11_DECODER_PROFILE_* of d3d11.h
	private static readonly Guid H264VldNoFgt = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
	private static readonly Guid HevcVldMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
	private static readonly Guid HevcVldMain10 = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
	private static readonly Guid Vp9VldProfile0 = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
	private static readonly Guid Vp9Vld10BitProfile2 = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");
	private static readonly Guid Av1VldProfile0 = new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");

	private static readonly (HardwareDecoder Decoder, Guid Profile, Format Format)[] DxvaDecoders =
	[
		(HardwareDecoder.H264, H264VldNoFgt, Format.NV12),
		(HardwareDecoder.Hevc, HevcVldMain, Format.NV12),
		(HardwareDecoder.Hevc10Bit, HevcVldMain10, Format.P010),
		(HardwareDecoder.Vp9, Vp9VldProfile0, Format.NV12),
		(HardwareDecoder.Vp9Profile2, Vp9Vld10BitProfile2, Format.P010),
		(HardwareDecoder.Av1, Av1VldProfile0, Format.NV12),
		(HardwareDecoder.Av1TenBit, Av1VldProfile0, Format.P010),
	];

	/// <summary>
	/// The picture sizes tried on each hardware decoder, largest first, up to DCI 8K. Picture heights are coded in macroblocks
	/// (16 lines), so 1080p is decoded as 1088 lines.
	/// </summary>
	private static readonly VideoSize[] CandidateSizes =
	[
		new(8192, 4320),
		new(7680, 4320),
		new(4096, 2304),
		new(4096, 2176),
		new(3840, 2160),
		new(2560, 1440),
		new(1920, 1088),
		new(1280, 720),
	];

	public static async Task<WmpCapabilities> ProbeAsync()
	{
		return new WmpCapabilities
		{
			VideoDecoders = await FindDecodersAsync(CodecKind.Video, WmpSubtypes.Video),
			AudioDecoders = await FindDecodersAsync(CodecKind.Audio, WmpSubtypes.Audio),
			HardwareDecoders = ProbeHardwareDecoders(),
			IsHdrDisplay = IsHdrDisplay(),
			AudioChannelCount = await GetAudioChannelCountAsync(),
		};
	}

	/// <summary>
	/// The decoders Media Foundation would load, the in-box ones and the ones of the codec extensions from the Store (HEVC, VP9, AV1,
	/// Web Media Extensions). Each subtype is queried on its own, querying all video decoders at once crashed on the Xbox.
	/// </summary>
	private static async Task<IReadOnlySet<Guid>> FindDecodersAsync(CodecKind kind, IEnumerable<Guid> subtypes)
	{
		HashSet<Guid> found = [];
		try
		{
			CodecQuery codecQuery = new();
			foreach (Guid subtype in subtypes)
			{
				try
				{
					var codecs = await codecQuery.FindAllAsync(kind, CodecCategory.Decoder, subtype.ToString("B"));
					if (codecs.Count > 0)
					{
						found.Add(subtype);
					}
				}
				catch
				{
					// a decoder that cannot be queried is treated as missing
				}
			}
		}
		catch
		{
			// CodecQuery is not available
		}

		return found;
	}

	/// <summary>
	/// Asks the GPU driver (DXVA) which decoder profiles it has and the largest picture each of them decodes, the way Chromium and
	/// Edge decide what they decode in hardware.
	/// </summary>
	private static Dictionary<HardwareDecoder, VideoSize> ProbeHardwareDecoders()
	{
		Dictionary<HardwareDecoder, VideoSize> result = [];
		try
		{
			FeatureLevel[] featureLevels =
			[
				FeatureLevel.Level_12_1,
				FeatureLevel.Level_12_0,
				FeatureLevel.Level_11_1,
				FeatureLevel.Level_11_0,
				FeatureLevel.Level_10_1,
				FeatureLevel.Level_10_0,
				FeatureLevel.Level_9_3,
			];
			if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.VideoSupport, featureLevels, out ID3D11Device? device).Failure
				|| device is null)
			{
				return result;
			}

			using (device)
			{
				using ID3D11VideoDevice? videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
				if (videoDevice is null)
				{
					return result;
				}

				HashSet<Guid> profiles = [];
				uint profileCount = videoDevice.VideoDecoderProfileCount;
				for (uint i = 0; i < profileCount; i++)
				{
					if (videoDevice.GetVideoDecoderProfile(i, out Guid profile).Success)
					{
						profiles.Add(profile);
					}
				}

				foreach (var (decoder, profile, format) in DxvaDecoders)
				{
					if (!profiles.Contains(profile)
						|| videoDevice.CheckVideoDecoderFormat(profile, format, out RawBool formatSupported).Failure
						|| !formatSupported)
					{
						continue;
					}

					foreach (VideoSize size in CandidateSizes)
					{
						if (CanDecode(videoDevice, profile, format, size))
						{
							result[decoder] = size;
							break;
						}
					}
				}
			}
		}
		catch
		{
			// no GPU, or no video support in the driver: no hardware decoding
		}

		return result;
	}

	/// <summary>
	/// A driver may list a configuration for a size that it then fails to create a decoder for, so the decoder is created as well.
	/// </summary>
	private static bool CanDecode(ID3D11VideoDevice videoDevice, Guid profile, Format format, VideoSize size)
	{
		try
		{
			VideoDecoderDescription description = new()
			{
				Guid = profile,
				SampleWidth = (uint)size.Width,
				SampleHeight = (uint)size.Height,
				OutputFormat = format,
			};

			if (videoDevice.GetVideoDecoderConfigCount(ref description, out uint configCount).Failure || configCount == 0)
			{
				return false;
			}

			if (videoDevice.GetVideoDecoderConfig(ref description, 0, out VideoDecoderConfig config).Failure)
			{
				return false;
			}

			if (videoDevice.CreateVideoDecoder(description, config, out ID3D11VideoDecoder? videoDecoder).Failure || videoDecoder is null)
			{
				return false;
			}

			videoDecoder.Dispose();
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Whether a display shows HDR, the Windows "Use HDR" setting is on for it (its desktop is in the BT.2100 PQ color space).
	/// </summary>
	private static bool IsHdrDisplay()
	{
		try
		{
			using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
			for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success; adapterIndex++)
			{
				using (adapter)
				{
					for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
					{
						using (output)
						{
							using IDXGIOutput6? output6 = output.QueryInterfaceOrNull<IDXGIOutput6>();
							if (output6?.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020)
							{
								return true;
							}
						}
					}
				}
			}
		}
		catch
		{
			// DXGI 1.6 is missing (Windows 10 before 1703): no HDR output
		}

		return false;
	}

	private static async Task<uint> GetAudioChannelCountAsync()
	{
		try
		{
			string defaultAudioRenderDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
			DeviceInformation defaultAudioDevice = await DeviceInformation.CreateFromIdAsync(defaultAudioRenderDeviceId);

			AudioGraphSettings settings = new(AudioRenderCategory.Media)
			{
				PrimaryRenderDevice = defaultAudioDevice
			};
			CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);

			if (result.Status == AudioGraphCreationStatus.Success)
			{
				using AudioGraph graph = result.Graph;
				return graph.EncodingProperties.ChannelCount;
			}
		}
		catch
		{
			// no audio device
		}

		// stereo, which every output plays, when the audio device cannot be read
		return 2;
	}
}
