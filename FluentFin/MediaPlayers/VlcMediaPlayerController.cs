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
		private static readonly Lazy<LibVLC> HeadlessLibVlc = new(() => new LibVLC(enableDebugLogs: false, "--vout=none"));

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
		private volatile bool _seekIssued;
		private volatile bool _disposed;
		private readonly PendingSeek _seek = new();
		private readonly Subject<TimeSpan> _seekPosition = new();
		private readonly Subject<Unit> _playing = new();
		private readonly Subject<Unit> _paused = new();
		private readonly IObservable<TimeSpan> _positionChanged;

		public VlcMediaPlayerController(IVideoView? view = null, string[]? swapChainOptions = null, Button? audioTracksButton = null)
		{
			_libVLC = view is null && swapChainOptions is null
				? HeadlessLibVlc.Value
				: new LibVLC(enableDebugLogs: false, swapChainOptions ?? []);
			_mp = new MediaPlayer(_libVLC);
			_audioTracksButton = audioTracksButton;
			if (view is not null)
			{
				view.MediaPlayer = _mp;
			}

			_libVLC.Log += OnLibVlcLog;
			_mp.Playing += (_, _) => OnBackendPlayingOrPaused();
			_mp.Paused += (_, _) => OnBackendPlayingOrPaused();
			_mp.SeekableChanged += (_, _) => OnSeekableChanged();
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
				if (_disposed)
				{
					return TimeSpan.Zero;
				}

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

		/// <summary>
		/// How far before the target VLC may be and still be taken as there. Opening an HLS stream at a start time, VLC first reports the
		/// start of the segment it begins with (4 s for a start at 6 s with segments of 4 s), within <see cref="PendingSeek.Tolerance"/>,
		/// and the time bar went back by up to a segment before it went on from the start time (MediaPlayerBehaviorTests). VLC plays on
		/// from where it landed, so it reaches the target soon also when the seek landed a little before it.
		/// </summary>
		private static readonly TimeSpan BeforeTheTarget = TimeSpan.FromSeconds(0.5);

		private bool ShouldReportPosition(TimeSpan actual) =>
			(actual > TimeSpan.Zero || _seek.Target is not { Ticks: > 0 })
			&& (_seek.Target is not { } target || actual >= target - BeforeTheTarget)
			&& _seek.ShouldReport(actual);

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

		private void OnSeekableChanged()
		{
			if (_disposed || _holding)
			{
				return;
			}

			if (!_seekIssued && _mp.IsSeekable && _seek.Target is { } target)
			{
				_seekIssued = true;
				ThreadPool.QueueUserWorkItem(_ =>
				{
					if (_disposed || _holding)
					{
						return;
					}

					try
					{
						_mp.SeekTo(target);
						if (!_isPlaying)
						{
							_mp.SetPause(true);
						}
					}
					catch
					{
					}
				});
			}
		}

		private void OnBackendPlayingOrPaused()
		{
			if (_disposed || _holding)
			{
				return;
			}

			_opened = true;
			OnSeekableChanged();
			ThreadPool.QueueUserWorkItem(_ =>
			{
				if (_disposed)
				{
					return;
				}

				try
				{
					if (!_isPlaying || _holding)
					{
						if (_mp.IsPlaying)
						{
							_mp.SetPause(true);
						}
					}
					else if (_mp.State is VLCState.Paused)
					{
						_mp.SetPause(false);
					}
				}
				catch
				{
				}
			});
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
			if (_mp.State is VLCState.Paused)
			{
				_mp.SetPause(false);
				return true;
			}

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
				_seekIssued = true;
			}
			else
			{
				_seekIssued = false;
			}

			if (!_isPlaying)
			{
				_media.AddOption(":start-paused");
			}

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
			_holding = false;
			_keepIntentOnOpen = false;
			_opened = false;
			_isPlaying = false;
			_seekIssued = false;
			_media = null;
			_mp.Stop();
		}

		public void HoldPosition(TimeSpan? position)
		{
			if (position is not { } held)
			{
				_holding = false;
				_keepIntentOnOpen = false;
				_seekIssued = false;
				_seek.Clear();
				return;
			}

			_holding = true;
			_opened = false;
			_seekIssued = false;
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
			// the start is the earliest a seek can go: a target before it is never reached and would hold the time bar there
			if (time < TimeSpan.Zero)
			{
				time = TimeSpan.Zero;
			}

			_holding = false;
			var sameTarget = _seek.Target == time;
			_seek.Set(time);
			_seekPosition.OnNext(time);
			if (sameTarget && _seekIssued)
			{
				return;
			}

			if (_mp.State is VLCState.Playing or VLCState.Paused && _mp.IsSeekable)
			{
				_seekIssued = true;
				_mp.SeekTo(time);
			}
			else
			{
				_seekIssued = false;
			}
		}

		public void Dispose()
		{
			// the held position is reminded on a timer, which reads the state and the time of the player: it stops before VLC is released,
			// a read of a released VLC player is an access violation that ends the process
			_disposed = true;
			_seek.Clear();
			_libVLC.Log -= OnLibVlcLog;
			_mp.Dispose();
		}

		public SourceAudioMenu? SourceAudioMenu { get; set; }

		public IEnumerable<Core.Contracts.Services.AudioTrack> GetAudioTracks() => _mp.Media?.Tracks.Where(x => x.TrackType == TrackType.Audio).Select(x => new Core.Contracts.Services.AudioTrack(x.Id, x.Language, x.Description)) ?? [];

		public MediaPlayerState State => _disposed ? MediaPlayerState.Stopped : ConvertState(_mp.State);

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
