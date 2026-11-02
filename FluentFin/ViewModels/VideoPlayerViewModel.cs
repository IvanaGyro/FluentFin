using System.Reactive.Disposables;
using System.Globalization;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.Services;
using Flurl;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace FluentFin.ViewModels;

public partial class VideoPlayerViewModel(IJellyfinClient jellyfinClient,
							TrickplayViewModel trickplayViewModel,
							ILogger<VideoPlayerViewModel> logger,
							IObservable<IOutboundWebSocketMessage> webSocketMessages,
							INavigationService navigationService,
							IPlaybackSessionManager playbackSessions,
							ISettings settings,
							ITaskBarProgress taskBarProgress) : ObservableObject, INavigationAware
{
	private readonly CompositeDisposable _disposables = [];
	private PlaybackSession? _session;
	private int _loadVersion;
	private KeyboardMediaPlayerController? _keyboardController;
	private PlayQueueUpdate? _playQueueUpdate;
	private TimeSpan _duration;
	private SendCommand? _previousCommand;


	public TrickplayViewModel TrickplayViewModel { get; } = trickplayViewModel;
	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;


	[ObservableProperty]
	public partial IMediaPlayerController? MediaPlayer { get; set; }

	[ObservableProperty]
	public partial List<MediaSegmentDto> Segments { get; set; } = [];

	[ObservableProperty]
	public partial bool IsSkipButtonVisible { get; set; }

	[ObservableProperty]
	public partial PlaylistViewModel Playlist { get; set; } = new PlaylistViewModel();

	[ObservableProperty]
	public partial MediaPlayerType MediaPlayerType { get; set; }

	public PlaybackProgressInfo_PlayMethod PlayMethod { get; private set; }

	public Action? ToggleFullScreen { get; set; }

	public Action<int>? SetProgress { get; set; }

	public async Task OnNavigatedFrom()
	{
		// invalidates any media that is still being loaded
		Interlocked.Increment(ref _loadVersion);
		Playlist.PropertyChanged -= OnPlaylistPropertyChanged;

		_disposables.Dispose();
		_keyboardController?.UnsubscribeEvents();

		NativeMethods.AllowSleep();

		taskBarProgress.Clear();
		await StopPlayback();
	}

	/// <summary>
	/// Releases the player's media source and reports the current playback session as stopped, which makes the server
	/// end its transcoding job. Safe to call repeatedly, only the first call for a session has an effect.
	/// </summary>
	private async Task StopPlayback(bool stopPlayer = true)
	{
		var session = Interlocked.Exchange(ref _session, null);
		long? position = null;

		try
		{
			// the position is lost once the player is stopped
			position = GetKnownPosition();

			if (stopPlayer)
			{
				MediaPlayer?.Stop();
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to stop the media player");
		}

		await playbackSessions.StopAsync(session, position);
	}

	public async Task OnNavigatedTo(object parameter)
	{
		MediaPlayerType = settings.MediaPlayer;

		if (parameter is BaseItemDto { Id: { } id })
		{
			await Initialize(id);
		}
		else if (parameter is PlaybackRequest { Dto.Id: { } requestedId } request)
		{
			await Initialize(requestedId);

			if (Playlist.Items.FirstOrDefault(x => x.Dto.Id == requestedId) is { } requestedItem)
			{
				requestedItem.Selection = request.Selection;
			}
		}
		else if (parameter is PlayQueueUpdate pqu)
		{
			_playQueueUpdate = pqu;
			InitializeForSyncPlay(pqu);
		}

		if (Playlist.Items.Count == 0)
		{
			return;
		}

		this.WhenAnyValue(x => x.MediaPlayer).WhereNotNull().Subscribe(SubscribeEvents);
		SubscribeWebsocketMessage();
		NativeMethods.PreventSleep();

		Playlist.PropertyChanged += OnPlaylistPropertyChanged;

		await TrickplayViewModel.Initialize();

		this.WhenAnyValue(x => x.MediaPlayer)
			.WhereNotNull()
			.Subscribe(mp =>
			{
				_keyboardController?.UnsubscribeEvents();
				_keyboardController = new KeyboardMediaPlayerController(mp, JellyfinClient, Skip, ToggleFullScreen);

				if (_playQueueUpdate is null)
				{
					Playlist.AutoSelect();
				}
				else
				{
					Playlist.SelectedItem = Playlist.Items.FirstOrDefault();
				}
			});

		Observable.Timer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20))
			.SelectMany(_ => UpdateStatus().ToObservable())
			.Subscribe()
			.DisposeWith(_disposables);
	}


	private async Task Initialize(Guid? id)
	{
		if (!id.HasValue)
		{
			return;
		}

		var dto = await JellyfinClient.GetItem(id.Value);

		if (dto is null)
		{
			return;
		}

		Playlist = dto.Type switch
		{
			BaseItemDto_Type.Movie => PlaylistViewModel.FromMovie(dto),
			BaseItemDto_Type.Episode => await PlaylistViewModel.FromEpisode(JellyfinClient, dto),
			BaseItemDto_Type.Series => await PlaylistViewModel.FromSeries(JellyfinClient, dto),
			BaseItemDto_Type.Season => await PlaylistViewModel.FromSeason(JellyfinClient, dto),
			_ => new PlaylistViewModel()
		};
	}

	private void InitializeForSyncPlay(PlayQueueUpdate pqu)
	{
		if (pqu.PlayingItemIndex is not >= 0)
		{
			return;
		}

		Playlist = PlaylistViewModel.FromSyncPlay(pqu.Playlist ?? []);
	}

	[RelayCommand]
	private async Task Skip()
	{
		if (MediaPlayer is null)
		{
			return;
		}

		var currentTime = MediaPlayer.Position.Ticks;
		var segment = Segments.FirstOrDefault(x => currentTime > x.StartTicks && currentTime < x.EndTicks);

		if (segment is not { EndTicks: not null })
		{
			return;
		}

		var endPosition = TimeSpan.FromTicks(segment.EndTicks.Value);

		if (_playQueueUpdate is not null)
		{
			await JellyfinClient.SignalSeekForSyncPlay(endPosition);
		}

		MediaPlayer.SeekTo(endPosition);
	}

	private async void OnPlaylistPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (MediaPlayer is null)
		{
			return;
		}

		if (e.PropertyName != nameof(Playlist.SelectedItem))
		{
			return;
		}

		await LoadSelectedItem();
	}

	/// <summary>Where to continue when the same item is loaded again with another audio stream, instead of the saved position.</summary>
	private long? _resumeTicks;

	/// <summary>
	/// Asks the server for the item again with the audio stream picked in the menu while transcoding, which plays it directly
	/// if the stream allows it, and continues at the current position.
	/// </summary>
	private async void SwitchAudioStream(int streamIndex)
	{
		if (Playlist.SelectedItem is not { } item || MediaPlayer is null)
		{
			return;
		}

		_resumeTicks = GetKnownPosition();
		item.Selection = new PlaybackSelection(streamIndex, item.Selection?.SubtitleStreamIndex);

		try
		{
			await LoadSelectedItem();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unable to switch to audio stream {Index}", streamIndex);
		}
	}

	private async Task LoadSelectedItem()
	{
		if (MediaPlayer is null)
		{
			return;
		}

		if (Playlist.SelectedItem is not { Dto.Id: not null } selectedItem)
		{
			return;
		}

		var version = Interlocked.Increment(ref _loadVersion);

		// clean up the previous session first, whether or not the new media can be loaded
		await StopPlayback();

		var full = await JellyfinClient.GetItem(selectedItem.Dto.Id.Value);

		if (full is null || version != _loadVersion)
		{
			return;
		}

		var mediaResponse = await GetMediaUrl(full, selectedItem.Selection);

		if (mediaResponse is null || version != _loadVersion)
		{
			return;
		}

		TrickplayViewModel.SetItem(full);

		var selection = selectedItem.Selection;
		var subtitleIndex = selection?.SubtitleStreamIndex ?? mediaResponse.MediaSourceInfo.DefaultSubtitleStreamIndex;
		var audioIndex = selection?.AudioStreamIndex ?? mediaResponse.MediaSourceInfo.DefaultAudioStreamIndex;
		selectedItem.Media = mediaResponse;

		// registered before playback starts so that leaving the page at any moment can clean it up
		var session = playbackSessions.Begin(selectedItem.Dto.Id.Value, mediaResponse);
		session.PositionProvider = GetKnownPosition;
		_session = session;

		MediaPlayer.SourceAudioMenu = TranscodeAudioMenu(mediaResponse, audioIndex);
		var audioOrdinal = DirectPlayAudioOrdinal(mediaResponse.PlayMethod, mediaResponse.MediaSourceInfo, audioIndex);

		LogMediaInfo(selectedItem.Dto, mediaResponse, selection, audioIndex, subtitleIndex);

		var success = MediaPlayer.Play(mediaResponse.Uri, audioIndex ?? 0, audioOrdinal);
		if (!success)
		{
			logger.LogError("Unable to open media from {URL} ({Playback})", mediaResponse.Uri.MaskApiKey(), DescribePlayback());
			await StopPlayback();
			return;
		}

		PlayMethod = mediaResponse.PlayMethod;

		var resumeTicks = _resumeTicks ?? selectedItem.Dto.UserData?.PlaybackPositionTicks;
		_resumeTicks = null;
		if (resumeTicks is { } ticks)
		{
			MediaPlayer.SeekTo(TimeSpan.FromTicks(ticks));
		}

		if (mediaResponse.MediaSourceInfo.MediaStreams?.FirstOrDefault(x => x.Type == MediaStream_Type.Subtitle && x.Index == subtitleIndex) is { } subtitleStream)
		{
			_ = OpenSubtitlesWhenReady(MediaPlayer, selectedItem, mediaResponse, subtitleStream);
		}
		else
		{
			logger.LogInformation("No subtitle opened at start: wanted stream index {Index} (selection={Selection})", subtitleIndex, selection?.SubtitleStreamIndex);
		}

		await playbackSessions.ReportStartAsync(session, GetPlaybackState(MediaPlayer));
	}

	/// <summary>
	/// A subtitle that is a track of the player can only be selected once the player knows its tracks, which is some time after
	/// opening the media for the backends that load them in the background.
	/// </summary>
	internal async Task OpenSubtitlesWhenReady(IMediaPlayerController mp, PlaylistItem item, MediaResponse response, MediaStream stream)
	{
		if (!stream.UsesDeliveryUrl(response.PlayMethod))
		{
			var subtitles = response.MediaSourceInfo.MediaStreams?.Where(x => x.Type == MediaStream_Type.Subtitle).ToList() ?? [];
			var ordinal = subtitles.Where(x => x.IsExternal is false).IndexOf(stream);

			for (var attempt = 0; attempt < 40 && mp.SubtitleTrackCount <= ordinal; attempt++)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(250));

				// another item or another stream of the same item has been started in the meantime
				if (!ReferenceEquals(item.Media, response))
				{
					return;
				}
			}

			if (mp.SubtitleTrackCount <= ordinal)
			{
				logger.LogWarning("The player reported {Count} subtitle tracks after 10 seconds, trying to open track {Ordinal} anyway", mp.SubtitleTrackCount, ordinal);
			}
		}

		OpenSubtitles(mp, response, stream);
	}

	private void OpenSubtitles(IMediaPlayerController mp, MediaResponse response, MediaStream stream)
	{
		if (stream is null)
		{
			return;
		}

		var subtitles = response?.MediaSourceInfo.MediaStreams?.Where(x => x.Type == MediaStream_Type.Subtitle).ToList() ?? [];

		try
		{
			if (stream.UsesDeliveryUrl(response!.PlayMethod))
			{
				var url = HttpUtility.UrlDecode(JellyfinClient.BaseUrl.AppendPathSegment(stream.DeliveryUrl).ToString());
				logger.LogInformation("Opening subtitle {Stream} as external file with {Player}, playMethod={PlayMethod}, url={Url}",
					stream.Describe(), mp.GetType().Name, response.PlayMethod, SubtitleStreamDiagnostics.WithoutToken(url));
				mp.OpenExternalSubtitleTrack(url);
			}
			else
			{
				var subtitleIndex = subtitles.Where(x => x.IsExternal is false).IndexOf(stream);
				if (stream.Index is not { } trackIndex)
				{
					logger.LogWarning("Not opening subtitle {Stream}: it has no stream index", stream.Describe());
					return;
				}

				logger.LogInformation("Opening subtitle {Stream} as internal track with {Player}, playMethod={PlayMethod}, trackIndex={TrackIndex}, ordinalAmongEmbedded={Ordinal}",
					stream.Describe(), mp.GetType().Name, response.PlayMethod, trackIndex, subtitleIndex);
				mp.OpenInternalSubtitleTrack(trackIndex, subtitleIndex);
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Unable to open subtitle {Stream} with {Player}", stream.Describe(), mp.GetType().Name);
		}
	}

	/// <summary>
	/// The position, among the audio tracks of the file, of the audio stream to play, for the player to switch to. A transcode already
	/// carries that stream; for direct play the player has to switch to it itself. It is the stream the user chose or, without a choice,
	/// the server's default (DefaultAudioStreamIndex, which follows the user's audio language preference), which need not be the default
	/// track of the file.
	/// </summary>
	internal static int? DirectPlayAudioOrdinal(PlaybackProgressInfo_PlayMethod playMethod, MediaSourceInfo source, int? audioStreamIndex)
	{
		if (playMethod != PlaybackProgressInfo_PlayMethod.DirectPlay || audioStreamIndex is not { } index)
		{
			return null;
		}

		int ordinal = source.MediaStreams?.Where(x => x.Type == MediaStream_Type.Audio).ToList().FindIndex(x => x.Index == index) ?? -1;
		return ordinal >= 0 ? ordinal : null;
	}

	/// <summary>The audio streams of the source file for the menu while transcoding, as the player only sees the stream of the transcode.</summary>
	internal SourceAudioMenu? TranscodeAudioMenu(MediaResponse response, int? audioIndex)
	{
		if (response.PlayMethod != PlaybackProgressInfo_PlayMethod.Transcode)
		{
			return null;
		}

		var tracks = BuildSourceAudioTracks(response.MediaSourceInfo);
		if (tracks.Count < 2)
		{
			return null;
		}

		var selected = audioIndex is { } index && tracks.Any(x => x.Id == index) ? index : tracks[0].Id;
		return new SourceAudioMenu(tracks, selected, track => SwitchAudioStream(track.Id));
	}

	internal static List<AudioTrack> BuildSourceAudioTracks(MediaSourceInfo source) =>
		source.MediaStreams?
			.Where(x => x.Type == MediaStream_Type.Audio && x.Index is not null)
			.Select(x => new AudioTrack(x.Index!.Value, LanguageName(x.Language), x.DisplayTitle ?? x.Codec))
			.ToList() ?? [];

	/// <summary>
	/// The players name the language of a track ("Chinese") while the server only has its ISO code ("zho"), the menu shows the
	/// name in both cases. A code that is not known is kept.
	/// </summary>
	internal static string? LanguageName(string? code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return code;
		}

		var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
			.FirstOrDefault(x => x.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
				|| x.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
		return culture?.EnglishName ?? code;
	}

	private void LogMediaInfo(BaseItemDto item, MediaResponse response, PlaybackSelection? selection, int? audioIndex, int? subtitleIndex)
	{
		var source = response.MediaSourceInfo;

		logger.LogInformation(
			"Playing {Name} ({Id}): method={PlayMethod}, container={Container}, bitrate={Bitrate}, size={Size}, directPlay={DirectPlay}, directStream={DirectStream}, transcoding={Transcoding}",
			item.Name, item.Id, response.PlayMethod, source.Container, source.Bitrate, source.Size, source.SupportsDirectPlay, source.SupportsDirectStream, source.SupportsTranscoding);

		logger.LogInformation(
			"Stream selection: page selection (audio={SelectedAudio}, subtitle={SelectedSubtitle}), server defaults (audio={DefaultAudio}, subtitle={DefaultSubtitle}), used (audio={UsedAudio}, subtitle={UsedSubtitle})",
			selection?.AudioStreamIndex, selection?.SubtitleStreamIndex, source.DefaultAudioStreamIndex, source.DefaultSubtitleStreamIndex, audioIndex, subtitleIndex);

		logger.LogInformation("Stream URL: {Url}", SubtitleStreamDiagnostics.WithoutToken(response.Uri.ToString()));

		if (response.PlayMethod == PlaybackProgressInfo_PlayMethod.Transcode)
		{
			logger.LogInformation("Transcode: container={Container}, protocol={Protocol}", source.TranscodingContainer, source.TranscodingSubProtocol);
		}

		foreach (var stream in source.MediaStreams ?? [])
		{
			logger.LogInformation(
				"Source stream #{Index} {Type}: codec={Codec}, language={Language}, title={Title}, {Width}x{Height}, channels={Channels}, bitrate={Bitrate}, default={Default}, external={External}",
				stream.Index, stream.Type, stream.Codec, stream.Language, stream.DisplayTitle, stream.Width, stream.Height, stream.Channels, stream.BitRate, stream.IsDefault, stream.IsExternal);

			if (stream.Type == MediaStream_Type.Subtitle)
			{
				logger.LogInformation("Subtitle delivery of {Stream}", stream.Describe());
			}
		}
	}


	private async Task<MediaResponse?> GetMediaUrl(BaseItemDto dto, PlaybackSelection? selection)
	{
		var segments = await JellyfinClient.GetMediaSegments(dto, [MediaSegmentType.Intro, MediaSegmentType.Outro]);

		if (segments is { Items: not null })
		{
			Segments = segments.Items;
		}

		return await JellyfinClient.GetMediaUrl(dto, selection);
	}

	// some backends keep reporting the position of the previous media until the new one is playing,
	// so the position only belongs to the current session once the player is actually playing it
	// a backend can also report Ended when it failed to open the media, there the position is zero and not a real position
	private long? GetKnownPosition() => MediaPlayer switch
	{
		{ State: MediaPlayerState.Playing or MediaPlayerState.Paused } mp => mp.Position.Ticks,
		{ State: MediaPlayerState.Ended, Position.Ticks: > 0 } mp => mp.Position.Ticks,
		_ => null,
	};

	private bool IsCurrentSession(PlaybackSession? session) => session is not null && ReferenceEquals(_session, session);

	private PlaybackState GetPlaybackState(IMediaPlayerController mp) => new(
		mp.Position.Ticks,
		!mp.IsPlaying,
		mp.IsMuted,
		mp.AudioTrackIndex,
		mp.SubtitleTrackIndex);

	private async Task UpdateStatus()
	{
		if (MediaPlayer is null || _session is not { } session)
		{
			return;
		}

		if (MediaPlayer.Position.Ticks == 0)
		{
			return;
		}

		if (MediaPlayer.State is MediaPlayerState.Error or MediaPlayerState.Ended or MediaPlayerState.Opening)
		{
			return;
		}

		taskBarProgress.SetProgressPercent((int)((MediaPlayer.Position.TotalSeconds / _duration.TotalSeconds) * 100));

		await playbackSessions.ReportProgressAsync(session, GetPlaybackState(MediaPlayer));
	}

	private string DescribePlayback()
	{
		var item = Playlist.SelectedItem;
		var media = item?.Media;

		// the players repeat the url in their error messages
		return ($"Item: {item?.Dto.Name} [{item?.Dto.Id}], " +
			$"PlaySessionId: {media?.PlaybackSessionId}, " +
			$"MediaSourceId: {media?.MediaSourceId}, " +
			$"PlayMethod: {media?.PlayMethod}, " +
			$"Container: {media?.MediaSourceInfo.Container}, " +
			$"Player: {MediaPlayerType}, " +
			$"PlayerState: {MediaPlayer?.State}, " +
			$"PlayerError: {MediaPlayer?.LastError}, " +
			$"Position: {MediaPlayer?.Position}, " +
			$"Url: {media?.Uri}").MaskApiKey();
	}

	private void SubscribeEvents(IMediaPlayerController mp)
	{
		mp.Playing.Where(_ => _disposables.IsDisposed).Subscribe(_ => mp.Stop());
		// the backends raise these on their own native threads, stopping a player from there can deadlock. The session is read where
		// the event is raised: one that arrives after its media has been replaced must not end the session of the new media
		mp.Ended.Select(_ => Volatile.Read(ref _session)).ObserveOn(RxSchedulers.MainThreadScheduler).Where(IsCurrentSession).Subscribe(async _ =>
		{
			if (Playlist.CanSelectNext)
			{
				// the previous session is stopped when the next item is selected
				taskBarProgress.Clear();
				Playlist.SelectNext();
			}
			else
			{
				await StopPlayback(stopPlayer: false);
			}
		});
		// events of a previous media can arrive late, only act on them if the player is still in that state
		mp.Stopped.ObserveOn(RxSchedulers.MainThreadScheduler).Where(_ => mp.State is MediaPlayerState.Stopped).Subscribe(async _ => await StopPlayback(stopPlayer: false));
		mp.Errored.Select(_ => Volatile.Read(ref _session)).ObserveOn(RxSchedulers.MainThreadScheduler).Where(IsCurrentSession).Subscribe(async _ =>
		{
			// described before the session is stopped, the backends do not report what went wrong
			logger.LogError("An error occurred while playing media ({Playback})", DescribePlayback());
			await StopPlayback();
		});
		mp.PositionChanged
			.Where(_ => MediaPlayer?.State == MediaPlayerState.Playing)
			.Select(x => x.Ticks)
			.Select(ticks => Segments.Any(segment => ticks > segment.StartTicks && ticks < segment.EndTicks))
			.DistinctUntilChanged()
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(isVisible => IsSkipButtonVisible = isVisible);
		mp.DurationChanged.Subscribe(d => _duration = d);

		mp.MediaLoaded
		  .Where(_ => _playQueueUpdate is not null)
		  .SelectMany(_ => JellyfinClient.SignalReadyForSyncPlay(new ReadyRequestDto
		  {
			  When = TimeProvider.System.GetUtcNow(),
			  IsPlaying = mp.IsPlaying,
			  PlaylistItemId = _playQueueUpdate!.Playlist![_playQueueUpdate.PlayingItemIndex ?? 0].PlaylistItemId,
			  PositionTicks = mp.Position.Ticks
		  }).ToObservable())
		  .Subscribe();
	}

	private void SubscribeWebsocketMessage()
	{
		webSocketMessages
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(async message =>
			{
				if (MediaPlayer is null)
				{
					return;
				}

				switch (message)
				{
					case PlaystateMessage { Data.Command: PlaystateRequest_Command.PlayPause }:
						await MediaPlayer.TogglePlayPlause(JellyfinClient);
						await UpdateStatus();
						break;
					case PlaystateMessage { Data.Command: PlaystateRequest_Command.Stop }:
						await StopPlayback();
						navigationService.NavigateTo<HomeViewModel>(new());
						break;
					case SyncPlayCommandMessage { Data: not null } syncPlay:

						if (syncPlay.Data.When == _previousCommand?.When)
						{
							return;
						}

						switch (syncPlay.Data.Command)
						{
							case SendCommand_Command.Pause:
								await SchedulePause(syncPlay.Data, MediaPlayer);
								break;
							case SendCommand_Command.Unpause:
								await SchedulePlay(syncPlay.Data, MediaPlayer);
								break;
							case SendCommand_Command.Seek:
								await SchedulePause(syncPlay.Data, MediaPlayer);
								break;
							case SendCommand_Command.Stop:
								await StopPlayback();
								break;
						}

						_previousCommand = syncPlay.Data;
						break;
				}
			})
			.DisposeWith(_disposables);
	}

	private async Task SchedulePause(SendCommand command, IMediaPlayerController mp)
	{
		if (command is not { When: { } commandTime, PositionTicks: { } positionTicks })
		{
			return;
		}

		var currentTime = await JellyfinClient.SyncTime();

		mp.SeekTo(new TimeSpan(positionTicks));
		if (commandTime > currentTime)
		{
			await Task.Delay(commandTime - currentTime);
			mp.Pause();
		}
		else
		{
			mp.Pause();
		}
	}

	private async Task SchedulePlay(SendCommand command, IMediaPlayerController mp)
	{
		if (command is not { When: { } commandTime, PositionTicks: { } positionTicks })
		{
			return;
		}

		// the play session was reported stopped, playing would restart its stream without being tracked,
		// after a stop the group restarts playback with a new play queue, which opens a new session
		var session = _session;
		if (session is null)
		{
			return;
		}

		var currentTime = await JellyfinClient.SyncTime();

		// a stop can arrive while waiting for the server time or for the time the command is scheduled for
		if (!ReferenceEquals(_session, session))
		{
			return;
		}

		if (commandTime > currentTime)
		{
			mp.SeekTo(new TimeSpan(positionTicks));
			await Task.Delay(commandTime - currentTime);

			if (!ReferenceEquals(_session, session))
			{
				return;
			}

			mp.Play();
		}
		else
		{
			var serverPosition = positionTicks + (currentTime - commandTime).Ticks;
			mp.SeekTo(new TimeSpan(serverPosition));
			mp.Play();
		}
	}
}

