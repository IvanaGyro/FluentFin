using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Core.Services;
using FluentFin.Core.Contracts.Services;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml.Controls;
using ReactiveMarbles.ObservableEvents;

namespace FluentFin.MediaPlayers
{
	internal sealed partial class VlcMediaPlayerController : IMediaPlayerController
	{
		private readonly LibVLC _libVLC;
		private readonly MediaPlayer _mp;
		private readonly Button _audioTracksButton;
		private Media? _media;
		private int _defaultAudioIndex;
		private int? _pendingAudioOrdinal;
		private readonly PendingSeek _seek = new();
		private readonly Subject<TimeSpan> _seekPosition = new();
		private readonly IObservable<TimeSpan> _positionChanged;

		public VlcMediaPlayerController(IVideoView view, string[] swapChainOptions, Button audioTracksButton)
		{
			_libVLC = new LibVLC(enableDebugLogs: false, swapChainOptions);
			_mp = new MediaPlayer(_libVLC);
			_audioTracksButton = audioTracksButton;
			view.MediaPlayer = _mp;
			_libVLC.Log += OnLibVlcLog;

			// shared, so that the seek is applied once per event whoever listens
			_positionChanged = Observable.Merge(
					_mp.Events().TimeChanged.Select(e => TimeSpan.FromMilliseconds(e.Time)).Do(ApplyPendingSeek).Where(_seek.ShouldReport),
					_seekPosition)
				.Publish()
				.RefCount();

			PositionChanged.Subscribe(p => Position = p);
		}

		private readonly Queue<string> _recentLog = new();

		// the error event has no details, LibVLC reports the reason (e.g. http errors) through its log
		public string? LastError
		{
			get
			{
				lock (_recentLog)
				{
					return _recentLog.Count == 0 ? null : string.Join(" | ", _recentLog);
				}
			}
		}

		private void OnLibVlcLog(object? sender, LogEventArgs e)
		{
			if (e.Level is not (LogLevel.Error or LogLevel.Warning))
			{
				return;
			}

			lock (_recentLog)
			{
				_recentLog.Enqueue($"{e.Level} {e.Module}: {e.Message}");
				while (_recentLog.Count > 8)
				{
					_recentLog.Dequeue();
				}
			}
		}

		public void OpenExternalSubtitleTrack(string url) => _mp.AddSlave(MediaSlaveType.Subtitle, url, true);
		public void OpenInternalSubtitleTrack(int trackIndex, int subtitleIndex) => _mp.SetSpu(trackIndex);
		public void DisableSubtitles() => _mp.SetSpu(-1);
		public void OpenAudioTrack(int index) => _mp.SetAudioTrack(index);
		public void Pause() => _mp.Pause();
		public bool Play() => _mp.Play();
		public bool Play(Uri uri, int defaultAudioIndex = 0, int? audioTrackOrdinal = null)
		{
			if (_media is not null)
			{
				_media.ParsedChanged -= MediaParsed;
			}

			// the position is cached from TimeChanged and would still be the one of the previous media
			_seek.Clear();
			Position = TimeSpan.Zero;
			lock (_recentLog)
			{
				_recentLog.Clear();
			}

			_media = new Media(_libVLC, uri);
			_defaultAudioIndex = defaultAudioIndex;
			_pendingAudioOrdinal = audioTrackOrdinal;
			_media.ParsedChanged += MediaParsed;
			return _mp.Play(_media);
		}

		private void ApplyPendingAudioTrack()
		{
			if (_pendingAudioOrdinal is not { } ordinal)
			{
				return;
			}

			var tracks = GetAudioTracks().ToList();
			if (ordinal < 0 || ordinal >= tracks.Count)
			{
				return;
			}

			_pendingAudioOrdinal = null;
			OpenAudioTrack(tracks[ordinal].Id);
		}

		public void Stop()
		{
			_seek.Clear();
			_mp.Stop();
		}

		/// <summary>
		/// Right after opening the media the player cannot seek yet and reports the start of the stream, which would reset the time bar
		/// for a few seconds. The seek is then kept, shown at once and made when the player can do it.
		/// </summary>
		public void SeekTo(TimeSpan time)
		{
			if (_mp.State is VLCState.Playing or VLCState.Paused && _mp.IsSeekable)
			{
				_seek.Clear();
				_mp.SeekTo(time);
				return;
			}

			_seek.Set(time);
			Position = time;
			_seekPosition.OnNext(time);
		}

		private void ApplyPendingSeek(TimeSpan reported)
		{
			if (_seek.Target is { } target && _seek.NeedsSeek(reported) && _mp.IsSeekable)
			{
				_mp.SeekTo(target);
			}
		}
		public void Dispose() => _mp.Dispose();
		public SourceAudioMenu? SourceAudioMenu { get; set; }

		public IEnumerable<Core.Contracts.Services.AudioTrack> GetAudioTracks() => _mp.Media?.Tracks.Where(x => x.TrackType == TrackType.Audio).Select(x => new Core.Contracts.Services.AudioTrack(x.Id, x.Language, x.Description)) ?? [];

		public MediaPlayerState State => ConvertState(_mp.State);
		public TimeSpan Position { get; private set; }
		public bool IsPlaying => _mp.IsPlaying;
		public bool IsMuted => _mp.Mute;
		public int? SubtitleTrackIndex => _mp.Spu == -1 ? null : _mp.Spu;
		public int? AudioTrackIndex => _mp.AudioTrack == -1 ? null : _mp.AudioTrack;

		public int SubtitleTrackCount
		{
			get
			{
				try
				{
					// the list always starts with the "Disable" entry (id -1)
					return _mp.SpuDescription.Count(x => x.Id >= 0);
				}
				catch
				{
					return 0;
				}
			}
		}
		public double Volume
		{
			get => _mp.Volume;
			set => _mp.Volume = (int)value;
		}

		public IObservable<TimeSpan> DurationChanged => _mp.Events().LengthChanged.Select(e => TimeSpan.FromMilliseconds(e.Length));
		public IObservable<TimeSpan> PositionChanged => _positionChanged;
		public IObservable<Unit> Playing => _mp.Events().Playing.Select(_ => Unit.Default);
		public IObservable<Unit> Paused => _mp.Events().Paused.Select(_ => Unit.Default);
		public IObservable<Unit> Ended => _mp.Events().EndReached.Select(_ => Unit.Default);
		public IObservable<Unit> Errored => _mp.Events().EncounteredError.Select(_ => Unit.Default);
		public IObservable<Unit> Stopped => _mp.Events().Stopped.Select(_ => Unit.Default);
		public IObservable<Unit> MediaLoaded => _mp.Events().MediaChanged.Select(_ => Unit.Default);
		public IObservable<double> VolumeChanged => _mp.Events().VolumeChanged.Select(e => e.Volume * 100d);
		public IObservable<string> SubtitleText { get; } = Observable.Empty<string>();

		private void MediaParsed(object? sender, MediaParsedChangedEventArgs e)
		{
			if (e.ParsedStatus == MediaParsedStatus.Done)
			{
				_audioTracksButton.DispatcherQueue.TryEnqueue(() =>
				{
					_audioTracksButton.Flyout = Converters.Converters.GetAudiosFlyout(this, _defaultAudioIndex);
					ApplyPendingAudioTrack();
				});
			}
		}

		private static MediaPlayerState ConvertState(VLCState state)
		{
			return state switch
			{
				VLCState.Opening => MediaPlayerState.Opening,
				VLCState.Playing => MediaPlayerState.Playing,
				VLCState.Paused => MediaPlayerState.Paused,
				VLCState.Stopped => MediaPlayerState.Stopped,
				VLCState.Ended => MediaPlayerState.Ended,
				VLCState.Error => MediaPlayerState.Error,
				_ => MediaPlayerState.Error,
			};
		}
	}
}
