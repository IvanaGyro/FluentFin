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

		var stream = source.MediaStreams?.FirstOrDefault(x => x.Type == MediaStream_Type.Audio && x.Index == streamIndex);
		var codec = stream?.Codec;
		if (stream is null || string.IsNullOrWhiteSpace(codec))
		{
			return false;
		}

		var listed = profile.DirectPlayProfiles?.Any(p =>
			p.Type == DirectPlayProfile_Type.Video
			&& Contains(p.Container, source.Container)
			&& Contains(p.AudioCodec, codec)) == true;

		return listed && MeetsCodecProfiles(profile, stream, codec);
	}

	/// <summary>
	/// The limits of the profile for the codec (channels, sample rate, ...) that the server would also check before it direct plays the
	/// stream. A condition on something the stream does not tell, or that is not known here, sends the question to the server when it is required.
	/// </summary>
	private static bool MeetsCodecProfiles(DeviceProfile profile, MediaStream stream, string codec)
	{
		foreach (var codecProfile in profile.CodecProfiles ?? [])
		{
			if (codecProfile.Type != CodecProfile_Type.VideoAudio
				|| (!string.IsNullOrWhiteSpace(codecProfile.Codec) && !Contains(codecProfile.Codec, codec)))
			{
				continue;
			}

			// the conditions to apply the profile: when they do not hold, the profile says nothing about this stream
			if (codecProfile.ApplyConditions is { Count: > 0 } apply && !apply.All(c => Satisfies(stream, c)))
			{
				continue;
			}

			if (codecProfile.Conditions is { Count: > 0 } conditions && !conditions.All(c => Satisfies(stream, c)))
			{
				return false;
			}
		}

		return true;
	}

	private static bool Satisfies(MediaStream stream, ProfileCondition condition)
	{
		string? actual = condition.Property switch
		{
			ProfileCondition_Property.AudioChannels => stream.Channels?.ToString(System.Globalization.CultureInfo.InvariantCulture),
			ProfileCondition_Property.AudioSampleRate => stream.SampleRate?.ToString(System.Globalization.CultureInfo.InvariantCulture),
			ProfileCondition_Property.AudioBitrate => stream.BitRate?.ToString(System.Globalization.CultureInfo.InvariantCulture),
			ProfileCondition_Property.AudioProfile => stream.Profile,
			_ => null,
		};

		// nothing to compare: it holds unless the profile requires it
		if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(condition.Value))
		{
			return condition.IsRequired != true;
		}

		var expected = condition.Value;
		var kind = condition.Condition?.ToString();
		var numbers = long.TryParse(actual, out var actualNumber) & long.TryParse(expected, out var expectedNumber);

		return kind switch
		{
			"LessThanEqual" => numbers ? actualNumber <= expectedNumber : false,
			"GreaterThanEqual" => numbers ? actualNumber >= expectedNumber : false,
			"NotEquals" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
			"EqualsAny" => expected.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(actual, StringComparer.OrdinalIgnoreCase),
			"Equals" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
			// a kind this does not know: the server decides
			_ => false,
		};
	}

	/// <summary>Whether the comma separated list has the value, in any case.</summary>
	private static bool Contains(string? list, string? value) =>
		!string.IsNullOrWhiteSpace(list)
		&& !string.IsNullOrWhiteSpace(value)
		&& list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
