using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

public sealed class PlaybackSelectionTests
{
	[Fact]
	public void From_NoneSubtitleEntry_IsNoSubtitles()
	{
		// the "None" entry of the subtitle picker is a stream without an index
		var none = new MediaStream();

		var selection = PlaybackSelection.From(new MediaStream { Index = 1 }, none);

		Assert.Equal(1, selection.AudioStreamIndex);
		Assert.Equal(PlaybackSelection.NoSubtitles, selection.SubtitleStreamIndex);
	}

	[Fact]
	public void From_RealSubtitle_KeepsItsIndex()
	{
		Assert.Equal(5, PlaybackSelection.From(null, new MediaStream { Index = 5 }).SubtitleStreamIndex);
	}

	[Fact]
	public void From_NothingPicked_UsesTheServerDefaults()
	{
		var selection = PlaybackSelection.From(null, null);

		Assert.Null(selection.AudioStreamIndex);
		Assert.Null(selection.SubtitleStreamIndex);
	}
}
