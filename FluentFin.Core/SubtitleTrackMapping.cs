using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>
/// Players number their tracks on their own, so a subtitle of the media source is found by its position among the subtitles
/// of the file, not by the stream index of the server.
/// </summary>
public static class SubtitleTrackMapping
{
	/// <summary>The position of the stream among the subtitles that are embedded in the file, -1 when it is not one of them.</summary>
	public static int EmbeddedSubtitleOrdinal(this MediaSourceInfo source, MediaStream stream)
	{
		var ordinal = 0;

		foreach (var candidate in source.MediaStreams ?? [])
		{
			if (candidate.Type != MediaStream_Type.Subtitle || candidate.IsExternal is not false)
			{
				continue;
			}

			if (ReferenceEquals(candidate, stream))
			{
				return ordinal;
			}

			ordinal++;
		}

		return -1;
	}

	/// <summary>The id of the n-th subtitle track of a player that lists its tracks with ids, the entry that turns subtitles off (a negative id) is skipped.</summary>
	public static int? IdOfNthSubtitle(IEnumerable<int> trackIds, int ordinal)
	{
		var ids = trackIds.Where(id => id >= 0).ToList();

		return ordinal >= 0 && ordinal < ids.Count ? ids[ordinal] : null;
	}
}
