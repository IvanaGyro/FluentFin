using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

public static class SubtitleStreamExtensions
{
	/// <summary>
	/// Whether the subtitle has to be loaded from the url the server provides instead of from a track of the player.
	/// A transcode only contains the audio and video, so an embedded text subtitle that the device profile wants delivered
	/// as an external file is not available as a track and only exists as a download.
	/// </summary>
	public static bool UsesDeliveryUrl(this MediaStream stream, PlaybackProgressInfo_PlayMethod playMethod)
	{
		return stream.IsExternal == true;
	}
}
