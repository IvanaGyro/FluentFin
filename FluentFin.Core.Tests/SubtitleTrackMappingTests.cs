using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

public sealed class SubtitleTrackMappingTests
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly BaseItemDto _movie = new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie, MediaType = BaseItemDto_MediaType.Video };

	[Fact]
	public async Task WindowsMediaPlayer_FindsTheSubtitleAfterChaptersAndImageSubtitles()
	{
		// the file: video 0, audio 1, text subtitles 2 and 3, image subtitle 4
		var response = await PlayFileWith(
			Stream(0, MediaStream_Type.Video, "h264"),
			Stream(1, MediaStream_Type.Audio, "eac3"),
			Stream(2, MediaStream_Type.Subtitle, "subrip"),
			Stream(3, MediaStream_Type.Subtitle, "subrip"),
			Stream(4, MediaStream_Type.Subtitle, "PGSSUB"));
		var image = SelectedStream(response, 4);
		var text = SelectedStream(response, 3);

		// what the player lists: a chapter track comes first, the image subtitle is a subtitle of its own kind
		string[] tracks = ["Chapter", "Subtitle", "Subtitle", "ImageSubtitle"];
		Func<string, bool> isSubtitle = kind => kind != "Chapter";

		Assert.Equal(3, SubtitleTrackMapping.PositionOfNthSubtitle(tracks, isSubtitle, response.MediaSourceInfo.EmbeddedSubtitleOrdinal(image)));

		// the second subtitle is at position 2, not at position 1 where the second track of the list is
		var ordinal = response.MediaSourceInfo.EmbeddedSubtitleOrdinal(text);
		Assert.Equal(1, ordinal);
		Assert.Equal(2, SubtitleTrackMapping.PositionOfNthSubtitle(tracks, isSubtitle, ordinal));
	}

	[Fact]
	public async Task Vlc_UsesItsOwnTrackIdInsteadOfTheStreamIndexOfTheServer()
	{
		// the file: video 0, audio 1 and 2, subtitles 3, 4 and 5
		var response = await PlayFileWith(
			Stream(0, MediaStream_Type.Video, "h264"),
			Stream(1, MediaStream_Type.Audio, "eac3"),
			Stream(2, MediaStream_Type.Audio, "aac"),
			Stream(3, MediaStream_Type.Subtitle, "subrip"),
			Stream(4, MediaStream_Type.Subtitle, "subrip"),
			Stream(5, MediaStream_Type.Subtitle, "subrip"));
		var selected = SelectedStream(response, 5);

		// what VLC lists: the entry that turns subtitles off, then its own numbering starting at 1
		int[] vlcIds = [-1, 1, 2, 3];

		var id = SubtitleTrackMapping.IdOfNthSubtitle(vlcIds, response.MediaSourceInfo.EmbeddedSubtitleOrdinal(selected));

		Assert.Equal(3, id);
		Assert.NotEqual(selected.Index, id);
	}

	private async Task<MediaResponse> PlayFileWith(params MediaStream[] streams)
	{
		var source = new MediaSourceInfo { Id = _movie.Id?.ToString("N"), Container = "mkv", MediaStreams = [.. streams] };
		AssertIsValid(source);

		_client
			.Setup(x => x.GetMediaUrl(It.IsAny<BaseItemDto>(), It.IsAny<PlaybackSelection?>()))
			.ReturnsAsync(new MediaResponse(new Uri("http://localhost:8096/Videos/stream"), PlaybackProgressInfo_PlayMethod.DirectPlay, "session", source.Id!, source));

		var response = await _client.Object.GetMediaUrl(_movie, null);

		return Assert.IsType<MediaResponse>(response);
	}

	private static MediaStream SelectedStream(MediaResponse response, int index) =>
		Assert.Single(response.MediaSourceInfo.MediaStreams!, x => x.Index == index);

	private static MediaStream Stream(int index, MediaStream_Type type, string codec) =>
		new() { Index = index, Type = type, Codec = codec, IsExternal = false };

	/// <summary>The server numbers the streams of a file one after the other and every stream has a kind and a codec.</summary>
	private static void AssertIsValid(MediaSourceInfo source)
	{
		var streams = Assert.IsType<List<MediaStream>>(source.MediaStreams);

		Assert.NotEmpty(streams);
		Assert.Equal(Enumerable.Range(0, streams.Count), streams.Select(x => x.Index!.Value));
		Assert.All(streams, stream =>
		{
			Assert.NotNull(stream.Type);
			Assert.False(string.IsNullOrEmpty(stream.Codec));
			Assert.False(stream.IsExternal);
		});
	}
}
