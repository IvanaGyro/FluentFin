using System.Globalization;
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
		private readonly Button? _audioTracksButton;
		private Media? _media;
		private int _defaultAudioIndex;
		private int? _pendingAudioOrdinal;
		private volatile bool _isPlaying;
		private volatile bool _opened;
		private volatile bool _holding;
		private volatile bool _keepIntentOnOpen;
		private volatile bool _disposed;
		private readonly PendingSeek _seek = new();
		private readonly Subject<TimeSpan> _seekPosition = new();
		private readonly Subject<Unit> _playing = new();
		private readonly Subject<Unit> _paused = new();
		private readonly IObservable<TimeSpan> _positionChanged;

		public VlcMediaPlayerController(IVideoView? view = null, string[]? swapChainOptions = null, Button? audioTracksButton = null)
		{
			_libVLC = new LibVLC(enableDebugLogs: false, swapChainOptions ?? []);
			_mp = new MediaPlayer(_libVLC);
			_audioTracksButton = audioTracksButton;
			if (view is not null)
			{
				view.MediaPlayer = _mp;
			}

			_libVLC.Log += OnLibVlcLog;
			_mp.Playing += (_, _) => ScheduleSyncBackendState();
			_mp.Paused += (_, _) => ScheduleSyncBackendState();
			_mp.SeekableChanged += (_, _) => ScheduleSyncBackendState();
			_mp.TimeChanged += (_, _) => ScheduleSyncBackendState();
			_mp.EndReached += (_, _) => _isPlaying = false;

			// shared, so that the seek is applied once per event whoever listens
			_positionChanged = Observable.Merge(
					_mp.Events().TimeChanged.Where(_ => CanEndSeek).Select(e => TimeSpan.FromMilliseconds(e.Time)).Where(ShouldReportPosition),
					_seekPosition,
					_seek.Reminders(TimeSpan.FromMilliseconds(250)))
				.Publish()
				.RefCount();
		}

		private bool CanEndSeek => !_holding && _opened;

		private TimeSpan ActualPosition
		{
			get
			{
				try
				{
					var time = _mp.Time;
					return time > 0 ? TimeSpan.FromMilliseconds(time) : TimeSpan.Zero;
				}
				catch
				{
					return TimeSpan.Zero;
				}
			}
		}

		private bool ShouldReportPosition(TimeSpan actual) =>
			(actual > TimeSpan.Zero || _seek.Target is not { Ticks: > 0 }) && _seek.ShouldReport(actual);

		private void SetIsPlaying(bool playing)
		{
			if (_isPlaying == playing)
			{
				return;
			}

			_isPlaying = playing;
			if (playing)
			{
				_playing.OnNext(Unit.Default);
			}
			else
			{
				_paused.OnNext(Unit.Default);
			}
		}

		private void ScheduleSyncBackendState()
		{
			if (_disposed)
			{
				return;
			}

			ThreadPool.QueueUserWorkItem(_ => SyncBackendState());
		}

		private void SyncBackendState()
		{
			if (_disposed)
			{
				return;
			}

			try
			{
				if (_holding)
				{
					if (_mp.IsPlaying)
					{
						_mp.SetPause(true);
					}

					return;
				}

				if (_mp.IsSeekable && _seek.Target is { } target)
				{
					var actual = ActualPosition;
					if (_seek.NeedsSeek(actual))
					{
						_mp.SeekTo(target);
					}
					else if (actual > TimeSpan.Zero || target == TimeSpan.Zero)
					{
						_opened = true;
					}
				}
				else if (_seek.Target is null && _mp.State is VLCState.Playing or VLCState.Paused)
				{
					_opened = true;
				}

				if (!_isPlaying && _mp.IsPlaying && (_seek.Target is null || (_mp.IsSeekable && !_seek.NeedsSeek(ActualPosition))))
				{
					_mp.SetPause(true);
				}
			}
			catch
			{
			}
		}

		private async Task PauseOnceSeekedAsync()
		{
			for (var i = 0; i < 60 && !_disposed && !_isPlaying && !_holding; i++)
			{
				await Task.Delay(50);
				SyncBackendState();
			}
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

		public void Pause()
		{
			if (_holding)
			{
				_keepIntentOnOpen = true;
			}

			SetIsPlaying(false);
			SyncBackendState();
			_ = PauseOnceSeekedAsync();
		}

		public bool Play()
		{
			if (_holding)
			{
				_keepIntentOnOpen = true;
				SetIsPlaying(true);
				return true;
			}

			if (_media is null)
			{
				return false;
			}

			SetIsPlaying(true);
			return _mp.Play();
		}

		public bool Play(Uri uri, int defaultAudioIndex = 0, int? audioTrackOrdinal = null)
		{
			if (_media is not null)
			{
				_media.ParsedChanged -= MediaParsed;
			}

			_holding = false;
			_opened = false;
			if (_keepIntentOnOpen)
			{
				_keepIntentOnOpen = false;
			}
			else
			{
				SetIsPlaying(true);
			}

			lock (_recentLog)
			{
				_recentLog.Clear();
			}

			_media = new Media(_libVLC, uri);
			if (_seek.Target is { } start && start > TimeSpan.Zero)
			{
				_media.AddOption($":start-time={start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
			}
			else if (!_isPlaying)
			{
				_media.AddOption(":start-paused");
			}

			_defaultAudioIndex = defaultAudioIndex;
			_pendingAudioOrdinal = audioTrackOrdinal;
			_media.ParsedChanged += MediaParsed;
			var result = _mp.Play(_media);
			if (!_isPlaying)
			{
				_ = PauseOnceSeekedAsync();
			}

			return result;
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
			_holding = false;
			_keepIntentOnOpen = false;
			_opened = false;
			_isPlaying = false;
			_media = null;
			_mp.Stop();
		}

		public void HoldPosition(TimeSpan? position)
		{
			if (position is not { } held)
			{
				_holding = false;
				_keepIntentOnOpen = false;
				_seek.Clear();
				return;
			}

			_holding = true;
			_opened = false;
			_keepIntentOnOpen = _media is not null || _keepIntentOnOpen;
			if (held > TimeSpan.Zero)
			{
				_seek.Set(held);
				_seekPosition.OnNext(held);
			}
			else
			{
				_seek.Clear();
			}

			try
			{
				if (_mp.IsPlaying)
				{
					_mp.SetPause(true);
				}
			}
			catch
			{
			}
		}

		/// <summary>
		/// Right after opening the media the player cannot seek yet and reports the start of the stream, which would reset the time bar
		/// for a few seconds. The seek is then kept, shown at once and made when the player can do it.
		/// </summary>
		public void SeekTo(TimeSpan time)
		{
			_holding = false;
			_seek.Set(time);
			_seekPosition.OnNext(time);
			if (_mp.State is VLCState.Playing or VLCState.Paused && _mp.IsSeekable)
			{
				_mp.SeekTo(time);
			}
		}

		public void Dispose()
		{
			_disposed = true;
			_mp.Dispose();
			_libVLC.Dispose();
		}

		public SourceAudioMenu? SourceAudioMenu { get; set; }

		public IEnumerable<Core.Contracts.Services.AudioTrack> GetAudioTracks() => _mp.Media?.Tracks.Where(x => x.TrackType == TrackType.Audio).Select(x => new Core.Contracts.Services.AudioTrack(x.Id, x.Language, x.Description)) ?? [];

		public MediaPlayerState State => ConvertState(_mp.State);

		public TimeSpan Position
		{
			get
			{
				var actual = ActualPosition;
				return CanEndSeek && ShouldReportPosition(actual) ? actual : _seek.Target ?? actual;
			}
		}

		public bool IsPlaying => _isPlaying;
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

		public IObservable<TimeSpan> DurationChanged => _mp.Events().LengthChanged.Select(e => TimeSpan.FromMilliseconds(e.Length)).Where(d => d > TimeSpan.Zero);
		public IObservable<TimeSpan> PositionChanged => _positionChanged;
		public IObservable<Unit> Playing => _playing;
		public IObservable<Unit> Paused => _paused;
		public IObservable<Unit> Ended => _mp.Events().EndReached.Select(_ => Unit.Default);
		public IObservable<Unit> Errored => _mp.Events().EncounteredError.Where(_ => !_holding).Select(_ => Unit.Default);
		public IObservable<Unit> Stopped => _mp.Events().Stopped.Select(_ => Unit.Default);
		public IObservable<Unit> MediaLoaded => _mp.Events().MediaChanged.Select(_ => Unit.Default);
		public IObservable<double> VolumeChanged => _mp.Events().VolumeChanged.Select(e => e.Volume * 100d);
		public IObservable<string> SubtitleText { get; } = Observable.Empty<string>();

		private void MediaParsed(object? sender, MediaParsedChangedEventArgs e)
		{
			if (e.ParsedStatus == MediaParsedStatus.Done)
			{
				if (_audioTracksButton?.DispatcherQueue is { } queue)
				{
					queue.TryEnqueue(() =>
					{
						_audioTracksButton.Flyout = Converters.Converters.GetAudiosFlyout(this, _defaultAudioIndex);
						ApplyPendingAudioTrack();
					});
				}
				else
				{
					ApplyPendingAudioTrack();
				}
			}
		}

		private static MediaPlayerState ConvertState(VLCState state)
		{
			return state switch
			{
				VLCState.NothingSpecial or VLCState.Opening or VLCState.Buffering => MediaPlayerState.Opening,
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
