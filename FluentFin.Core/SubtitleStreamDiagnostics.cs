using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

public static class SubtitleStreamDiagnostics
{
	/// <summary>One line for the log with what decides how a subtitle can be opened.</summary>
	public static string Describe(this MediaStream stream) =>
		$"#{stream.Index} codec={stream.Codec}, language={stream.Language}, title={stream.DisplayTitle}, isExternal={stream.IsExternal}, " +
		$"isTextSubtitle={stream.IsTextSubtitleStream}, delivery={stream.DeliveryMethod}, deliveryUrl={stream.DeliveryUrl.MaskApiKey()}";
}
