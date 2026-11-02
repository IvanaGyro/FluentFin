using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Services;

/// <summary>
/// Whether picking another audio stream can be done by the player or needs the server to answer again, as the web client does:
/// a transcode (or a stream the server remuxed) carries one audio stream and needs the server, a direct play carries all of them and
/// the player switches, if the device profile says it plays the codec of the stream. Otherwise the server decides (transcodes it).
/// </summary>
public static class AudioSwitchPolicy
{
	public static bool CanSwitchInPlayer(PlaybackProgressInfo_PlayMethod playMethod, MediaSourceInfo source, int streamIndex, DeviceProfile profile)
	{
		if (playMethod != PlaybackProgressInfo_PlayMethod.DirectPlay)
		{
			return false;
		}

		var codec = source.MediaStreams?.FirstOrDefault(x => x.Type == MediaStream_Type.Audio && x.Index == streamIndex)?.Codec;
		if (string.IsNullOrWhiteSpace(codec))
		{
			return false;
		}

		return profile.DirectPlayProfiles?.Any(p =>
			p.Type == DirectPlayProfile_Type.Video
			&& Contains(p.Container, source.Container)
			&& Contains(p.AudioCodec, codec)) == true;
	}

	/// <summary>Whether the comma separated list has the value, in any case.</summary>
	private static bool Contains(string? list, string? value) =>
		!string.IsNullOrWhiteSpace(list)
		&& !string.IsNullOrWhiteSpace(value)
		&& list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
