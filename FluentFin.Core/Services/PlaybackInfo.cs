using System.Globalization;
using System.Web;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Services;

public sealed record PlaybackInfoRow(string Label, string Value);

public sealed record PlaybackInfoSection(string Title, IReadOnlyList<PlaybackInfoRow> Rows);

/// <summary>What the player shows in its "Playback info" dialog, modeled on the Jellyfin web client.</summary>
public static class PlaybackInfo
{
	public static IReadOnlyList<PlaybackInfoSection> Build(MediaResponse media, int? audioStreamIndex, int? subtitleStreamIndex, string? playerName, TranscodingInfo? transcoding = null)
	{
		var source = media.MediaSourceInfo;
		var streams = source.MediaStreams ?? [];
		var isTranscode = media.PlayMethod == PlaybackProgressInfo_PlayMethod.Transcode;
		var sections = new List<PlaybackInfoSection>();

		var video = streams.FirstOrDefault(x => x.Type == MediaStream_Type.Video);
		var audio = (audioStreamIndex is { } a ? streams.FirstOrDefault(x => x.Type == MediaStream_Type.Audio && x.Index == a) : null)
			?? streams.FirstOrDefault(x => x.Type == MediaStream_Type.Audio);
		var subtitle = subtitleStreamIndex is >= 0 and var s
			? streams.FirstOrDefault(x => x.Type == MediaStream_Type.Subtitle && x.Index == s)
			: null;

		var playback = new List<PlaybackInfoRow>();
		Add(playback, "Player", playerName);
		Add(playback, "Play method", DescribePlayMethod(media.PlayMethod));
		Add(playback, "Protocol", isTranscode ? source.TranscodingSubProtocol?.ToString() : "http");
		Add(playback, "Stream type", isTranscode ? "Transcode" : media.PlayMethod.ToString());
		Add(playback, "Container", source.Container);
		Add(playback, "Size", FormatSize(source.Size));
		Add(playback, "Bitrate", FormatBitrate(source.Bitrate));
		Add(playback, "Play session ID", media.PlaybackSessionId);
		Add(playback, "Media source ID", media.MediaSourceId);
		sections.Add(new("Playback info", playback));

		if (isTranscode)
		{
			var transcode = new List<PlaybackInfoRow>();
			Add(transcode, "Container", transcoding?.Container ?? source.TranscodingContainer);
			Add(transcode, "Protocol", source.TranscodingSubProtocol?.ToString());
			Add(transcode, "Video", DescribeStream(transcoding?.VideoCodec, transcoding?.IsVideoDirect));
			Add(transcode, "Audio", DescribeStream(transcoding?.AudioCodec, transcoding?.IsAudioDirect));
			Add(transcode, "Resolution", transcoding?.Width is > 0 && transcoding.Height is > 0 ? $"{transcoding.Width}x{transcoding.Height}" : null);
			Add(transcode, "Bitrate", FormatBitrate(transcoding?.Bitrate));
			Add(transcode, "Frame rate", transcoding?.Framerate is { } fps and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fps:0.###} fps") : null);
			Add(transcode, "Audio channels", transcoding?.AudioChannels is { } ch and > 0 ? ch.ToString(CultureInfo.InvariantCulture) : null);
			Add(transcode, "Hardware acceleration", transcoding?.HardwareAccelerationType?.ToString());
			Add(transcode, "Progress", transcoding?.CompletionPercentage is { } done ? string.Create(CultureInfo.InvariantCulture, $"{done:0.#}%") : null);

			var reasons = transcoding?.TranscodeReasons is { Count: > 0 } live
				? live.Select(x => x.ToString() ?? "").ToList()
				: TranscodeReasons(source.TranscodingUrl).ToList();
			Add(transcode, reasons.Count > 1 ? "Reasons" : "Reason", string.Join('\n', reasons.Where(x => x.Length > 0).Select(Humanize)));
			sections.Add(new("Transcoding info", transcode));
		}

		if (video is not null)
		{
			var rows = new List<PlaybackInfoRow>();
			Add(rows, "Title", video.DisplayTitle);
			Add(rows, "Codec", video.Codec?.ToUpperInvariant());
			Add(rows, "Profile", video.Profile);
			Add(rows, "Resolution", video.Width is > 0 && video.Height is > 0 ? $"{video.Width}x{video.Height}" : null);
			Add(rows, "Bitrate", FormatBitrate(video.BitRate));
			Add(rows, "Frame rate", video.RealFrameRate is { } fps and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fps:0.###} fps") : null);
			Add(rows, "Bit depth", video.BitDepth is { } depth and > 0 ? $"{depth} bit" : null);
			Add(rows, "Dynamic range", video.VideoRangeType?.ToString());
			sections.Add(new("Video", rows));
		}

		if (audio is not null)
		{
			var rows = new List<PlaybackInfoRow>();
			Add(rows, "Title", audio.DisplayTitle);
			Add(rows, "Language", audio.Language);
			Add(rows, "Codec", audio.Codec?.ToUpperInvariant());
			Add(rows, "Channels", audio.ChannelLayout ?? (audio.Channels is { } channels and > 0 ? channels.ToString(CultureInfo.InvariantCulture) : null));
			Add(rows, "Bitrate", FormatBitrate(audio.BitRate));
			Add(rows, "Sample rate", audio.SampleRate is { } rate and > 0 ? $"{rate} Hz" : null);
			Add(rows, "Bit depth", audio.BitDepth is { } bits and > 0 ? $"{bits} bit" : null);
			sections.Add(new("Audio", rows));
		}

		if (subtitle is not null)
		{
			var rows = new List<PlaybackInfoRow>();
			Add(rows, "Title", subtitle.DisplayTitle);
			Add(rows, "Language", subtitle.Language);
			Add(rows, "Codec", subtitle.Codec?.ToUpperInvariant());
			Add(rows, "External", subtitle.IsExternal == true ? "Yes" : "No");
			sections.Add(new("Subtitles", rows));
		}

		return sections;
	}

	/// <summary>The reasons the server put into the transcoding url, empty when the stream is not transcoded.</summary>
	public static IReadOnlyList<string> TranscodeReasons(string? transcodingUrl)
	{
		if (string.IsNullOrEmpty(transcodingUrl))
		{
			return [];
		}

		var queryStart = transcodingUrl.IndexOf('?');
		if (queryStart < 0)
		{
			return [];
		}

		var value = HttpUtility.ParseQueryString(transcodingUrl[(queryStart + 1)..])["TranscodeReasons"];
		return string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	/// <summary>"AudioCodecNotSupported" becomes "Audio codec not supported".</summary>
	public static string Humanize(string reason)
	{
		var text = System.Text.RegularExpressions.Regex.Replace(reason, "(?<=[a-z0-9])(?=[A-Z])", " ");
		return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
	}

	public static string? FormatBitrate(long? bitsPerSecond) => bitsPerSecond switch
	{
		null or <= 0 => null,
		>= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond.Value / 1_000_000d:0.#} Mbps"),
		_ => string.Create(CultureInfo.InvariantCulture, $"{bitsPerSecond.Value / 1000d:0} kbps"),
	};

	public static string? FormatBitrate(int? bitsPerSecond) => FormatBitrate((long?)bitsPerSecond);

	public static string? FormatSize(long? bytes) => bytes switch
	{
		null or <= 0 => null,
		>= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes.Value / (double)(1L << 30):0.##} GiB"),
		_ => string.Create(CultureInfo.InvariantCulture, $"{bytes.Value / (double)(1L << 20):0.#} MiB"),
	};

	private static string DescribePlayMethod(PlaybackProgressInfo_PlayMethod method) => method switch
	{
		PlaybackProgressInfo_PlayMethod.DirectPlay => "Direct play",
		PlaybackProgressInfo_PlayMethod.DirectStream => "Direct stream",
		PlaybackProgressInfo_PlayMethod.Transcode => "Transcoding",
		_ => method.ToString(),
	};

	private static string? DescribeStream(string? codec, bool? isDirect) =>
		string.IsNullOrEmpty(codec) ? null : $"{codec.ToUpperInvariant()} ({(isDirect == true ? "direct" : "transcoded")})";

	private static void Add(List<PlaybackInfoRow> rows, string label, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			rows.Add(new(label, value));
		}
	}
}
