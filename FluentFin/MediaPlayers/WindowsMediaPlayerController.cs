using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using FluentFin.Core.Contracts.Services;
using Microsoft.UI.Xaml.Controls;
using ReactiveMarbles.ObservableEvents;
using FluentFin.Core;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;

namespace FluentFin.MediaPlayers
{
	public static class MPExtensions
	{
		public static T SafeGetValue<T>(this Windows.Media.Playback.MediaPlayer mp, Func<Windows.Media.Playback.MediaPlayer, T> getter, T defaultValue)
		{
			try
			{
				return getter(mp);
			}
			catch
			{
				return defaultValue;
			}
		}
	}

	public sealed partial class WindowsMediaPlayerController : IMediaPlayerController
	{
		private readonly Windows.Media.Playback.MediaPlayer _mp = new() { AutoPlay = false };
		private Windows.Media.Playback.MediaPlaybackItem? _mediaItem;
		private int? _wantedAudioOrdinal;
		private readonly Button _audioTracksButton;
		private readonly WmpSeeker _seeker;
		private readonly ILogger _logger = App.GetService<ILoggerFactory>().CreateLogger("FluentFin.WindowsMediaPlayer");

		public WindowsMediaPlayerController(MediaPlayerElement element, Button audioTracksButton)
		{
			_audioTracksButton = audioTracksButton;
			_seeker = new WmpSeeker(_mp);
			element.AreTransportControlsEnabled = false;
			element.SetMediaPlayer(_mp);
			_mp.MediaFailed += (_, e) =>
			{
				LastError = $"{e.Error}: {e.ErrorMessage} (HRESULT 0x{e.ExtendedErrorCode?.HResult ?? 0:X8})";
				_logger.LogError("Media failed: {Error}", LastError);
			};
			_mp.MediaOpened += (_, _) =>
			{
				_logger.LogInformation("Media opened: duration={Duration}, volume={Volume}, muted={Muted}", _mp.SafeGetValue(m => m.NaturalDuration, TimeSpan.Zero), Volume, IsMuted);
				_audioTracksButton.DispatcherQueue.TryEnqueue(() =>
				{
					SelectWantedAudioTrack("media opened");
					_seeker.NotifyOpened();
				});
			};
			_mp.PlaybackSession.PlaybackStateChanged += (s, _) => _logger.LogInformation("Playback state: {State}", s.PlaybackState);
		}

		public string? LastError { get; private set; }

		public MediaPlayerState State => ConvertState(_mp.SafeGetValue(mp => mp.CurrentState, Windows.Media.Playback.MediaPlayerState.Closed));
		public TimeSpan Position => _seeker.Position;
		public bool IsPlaying => _mp.SafeGetValue(mp => mp.CurrentState, Windows.Media.Playback.MediaPlayerState.Closed) is Windows.Media.Playback.MediaPlayerState.Playing;
		public bool IsMuted => _mp.SafeGetValue(mp => mp.IsMuted, false);
		public int? SubtitleTrackIndex => null;
		public int? AudioTrackIndex => null;

		// text subtitles, image subtitles (PGS, VobSub) and closed captions; the other kinds are chapters and other metadata
		public int SubtitleTrackCount => _mediaItem?.TimedMetadataTracks.Count(x =>
			x.TimedMetadataKind is TimedMetadataKind.Subtitle or TimedMetadataKind.ImageSubtitle or TimedMetadataKind.Caption) ?? 0;
		public double Volume
		{
			get
			{
				try
				{
					return _mp.Volume;
				}
				catch
				{
					return 0;
				}
			}
			set
			{
				try
				{
					_mp.Volume = value;
				}
				catch { }
			}
		}


		public IObservable<TimeSpan> DurationChanged => _mp.SafeGetValue(mp => mp.PlaybackSession, null)?.Events().NaturalDurationChanged.Select(_ => _mp.SafeGetValue(mp => mp.NaturalDuration, TimeSpan.Zero)) ?? Observable.Empty<TimeSpan>();
		public IObservable<TimeSpan> PositionChanged => _seeker.PositionChanged;
		public IObservable<Unit> Playing => _mp.PlaybackSession.Events().PlaybackStateChanged.Where(_ => _mp.CurrentState is Windows.Media.Playback.MediaPlayerState.Playing).Select(_ => Unit.Default);
		public IObservable<Unit> Paused => _mp.PlaybackSession.Events().PlaybackStateChanged.Where(_ => _mp.CurrentState is Windows.Media.Playback.MediaPlayerState.Paused).Select(_ => Unit.Default);
		public IObservable<Unit> Ended => _mp.Events().MediaEnded.Select(_ => Unit.Default);
		public IObservable<Unit> Errored => _mp.Events().MediaFailed.Select(_ => Unit.Default);
		public IObservable<Unit> Stopped => _mp.PlaybackSession.Events().PlaybackStateChanged.Where(_ => _mp.CurrentState is Windows.Media.Playback.MediaPlayerState.Stopped).Select(_ => Unit.Default);
		public IObservable<Unit> MediaLoaded => _mp.Events().MediaOpened.Select(_ => Unit.Default);
		public IObservable<double> VolumeChanged => _mp.Events().VolumeChanged.Select(_ => _mp.Volume);
		public IObservable<string> SubtitleText { get; } = Observable.Empty<string>();

		public void Dispose() => _mp.Dispose();

		public SourceAudioMenu? SourceAudioMenu { get; set; }

		public IEnumerable<Core.Contracts.Services.AudioTrack> GetAudioTracks()
		{
			if (_mediaItem is null)
			{
				return [];
			}

			return _mediaItem.AudioTracks.Select(x => new Core.Contracts.Services.AudioTrack(_mediaItem.AudioTracks.IndexOf(x), x.Language, x.Label));
		}

		public void OpenAudioTrack(int index)
		{
			if (_mediaItem is null)
			{
				return;
			}

			_logger.LogInformation("Selecting audio track {Index} of {Count} (selected before: {Before})", index, _mediaItem.AudioTracks.Count, _mediaItem.AudioTracks.SelectedIndex);
			_mediaItem.AudioTracks.SelectedIndex = index;
			_logger.LogInformation("Selected audio track is now {After}", _mediaItem.AudioTracks.SelectedIndex);
		}

		public void OpenExternalSubtitleTrack(string url)
		{
			if (_mediaItem is null)
			{
				return;
			}

			var track = TimedTextSource.CreateFromUri(new Uri(url));
			_mediaItem.Source.ExternalTimedTextSources.Add(track);
			track.Resolved += Track_Resolved;
		}

		public void OpenInternalSubtitleTrack(int trackIndex, int subtitleIndex)
		{
			if (_mediaItem is null or { TimedMetadataTracks: null })
			{
				return;
			}

			var timedMetadataTracks = _mediaItem.TimedMetadataTracks;
			if (subtitleIndex >= 0 && subtitleIndex < timedMetadataTracks.Count)
			{
				foreach (var track in timedMetadataTracks.Index())
				{
					_mediaItem.TimedMetadataTracks.SetPresentationMode((uint)track.Index, Windows.Media.Playback.TimedMetadataTrackPresentationMode.Disabled);
				}

				_mediaItem.TimedMetadataTracks.SetPresentationMode((uint)subtitleIndex, Windows.Media.Playback.TimedMetadataTrackPresentationMode.PlatformPresented);
			}
		}

		public void DisableSubtitles()
		{
			if (_mediaItem is null or { TimedMetadataTracks: null })
			{
				return;
			}

			var timedMetadataTracks = _mediaItem.TimedMetadataTracks;
			foreach (var track in timedMetadataTracks.Index())
			{
				_mediaItem.TimedMetadataTracks.SetPresentationMode((uint)track.Index, Windows.Media.Playback.TimedMetadataTrackPresentationMode.Disabled);
			}
		}

		public void Pause()
		{
			try
			{
				_mp.Pause();
			}
			catch { }
		}

		public bool Play()
		{
			try
			{
				// Stop() released the source, the play session of that media has ended on the server
				// and must not be reused, new media has to be opened with Play(Uri) instead
				if (_mp.Source is null)
				{
					return false;
				}

				_mp.Play();
			}
			catch { }
			return true;
		}

		public bool Play(Uri uri, int defaultAudioIndex = 0, int? audioTrackOrdinal = null)
		{
			_wantedAudioOrdinal = audioTrackOrdinal;
			LastError = null;
			_logger.LogInformation("Opening {Url} (audio ordinal={Ordinal}, default audio index={Default})", uri.MaskApiKey(), audioTrackOrdinal, defaultAudioIndex);
			var source = MediaSource.CreateFromUri(uri);
			_mediaItem = new Windows.Media.Playback.MediaPlaybackItem(source);
			_mediaItem.AudioTracksChanged += (sender, args) => _audioTracksButton.DispatcherQueue.TryEnqueue(() =>
			{
				LogAudioTracks();
				_audioTracksButton.Flyout = Converters.Converters.GetAudiosFlyout(this, defaultAudioIndex);
			});
			_mp.Source = _mediaItem;
			_mp.Play();
			return true;
		}

		private void LogAudioTracks()
		{
			if (_mediaItem is null)
			{
				return;
			}

			_logger.LogInformation("Audio tracks changed: {Count} tracks, selected={Selected}", _mediaItem.AudioTracks.Count, _mediaItem.AudioTracks.SelectedIndex);
			foreach (var (track, index) in _mediaItem.AudioTracks.Select((t, i) => (t, i)))
			{
				try
				{
					var encoding = track.GetEncodingProperties();
					_logger.LogInformation(
						"Audio track #{Index}: language={Language}, label={Label}, subtype={Subtype}, channels={Channels}, sampleRate={SampleRate}, bitrate={Bitrate}, decoder={Decoder}, source={Status}",
						index, track.Language, track.Label, encoding.Subtype, encoding.ChannelCount, encoding.SampleRate, encoding.Bitrate, track.SupportInfo.DecoderStatus, track.SupportInfo.MediaSourceStatus);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Audio track #{Index}: unable to read its properties", index);
				}
			}
		}

		/// <summary>
		/// Selects the audio track the item is to play, once the media is open. A track selected while the tracks are announced
		/// (AudioTracksChanged) shows as selected, with a decoder that supports it, and plays no sound at all, measured on the
		/// audio device (WmpAudioPlaybackTests). Selecting it once the media is open, or later, does.
		/// </summary>
		private void SelectWantedAudioTrack(string reason)
		{
			if (_mediaItem is null || _wantedAudioOrdinal is not { } index)
			{
				return;
			}

			var tracks = GetAudioTracks().ToList();
			if (index < 0 || index >= tracks.Count)
			{
				_logger.LogWarning("Not selecting the audio track: ordinal {Ordinal} of {Count} tracks", index, tracks.Count);
				return;
			}

			_wantedAudioOrdinal = null;
			_logger.LogInformation("Selecting the audio track ({Reason}): {Index}", reason, index);
			OpenAudioTrack(tracks[index].Id);
		}

		public void SeekTo(TimeSpan timeSpan) => _seeker.SeekTo(timeSpan);

		public void HoldPosition(TimeSpan? position) => _seeker.Hold(position);

		/// <summary>
		/// Unlike <see cref="Pause"/>, releases the media source so that the network stream is closed.
		/// </summary>
		public void Stop()
		{
			_logger.LogInformation("Stopping, releasing the media source");
			try
			{
				_mp.Pause();
				_mp.Source = null;
			}
			catch { }

			_mediaItem = null;
		}

		private static MediaPlayerState ConvertState(Windows.Media.Playback.MediaPlayerState state)
		{
			return state switch
			{
				Windows.Media.Playback.MediaPlayerState.Opening => MediaPlayerState.Opening,
				Windows.Media.Playback.MediaPlayerState.Playing => MediaPlayerState.Playing,
				Windows.Media.Playback.MediaPlayerState.Paused => MediaPlayerState.Paused,
				Windows.Media.Playback.MediaPlayerState.Stopped => MediaPlayerState.Stopped,
				_ => MediaPlayerState.Error,
			};
		}

		private void Track_Resolved(TimedTextSource sender, TimedTextSourceResolveResultEventArgs args)
		{
			var index = args.Tracks[0].PlaybackItem.TimedMetadataTracks.IndexOf(args.Tracks[0]);
			args.Tracks[0].PlaybackItem.TimedMetadataTracks.SetPresentationMode((uint)index, Windows.Media.Playback.TimedMetadataTrackPresentationMode.PlatformPresented);
			sender.Resolved -= Track_Resolved;
		}
	}
}
