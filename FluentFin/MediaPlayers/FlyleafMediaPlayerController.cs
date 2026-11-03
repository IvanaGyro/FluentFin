using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Web;
using DynamicData.Binding;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FlyleafLib.Controls.WinUI;
using FlyleafLib.MediaPlayer;
using Microsoft.UI.Xaml.Controls;
using ReactiveUI;

namespace FluentFin.MediaPlayers;

public sealed partial class FlyleafMediaPlayerController : IMediaPlayerController
{
	private readonly Player _mp = new();
	private readonly PendingSeek _seek = new();
	private readonly Subject<TimeSpan> _seekPosition = new();
	private readonly Subject<Unit> _playing = new();
	private readonly Subject<Unit> _paused = new();
	private readonly IObservable<TimeSpan> _positionChanged;
	private readonly Button? _audiosFlyoutButton;
	private int _defaultAudioIndex;
	private int? _pendingAudioOrdinal;
	private volatile bool _isPlaying;
	private volatile bool _hasMedia;
	private volatile bool _opened;
	private volatile bool _holding;
	private volatile bool _keepIntentOnOpen;
	private TimeSpan? _lastSeekIssued;

	public FlyleafMediaPlayerController(FlyleafHost? host = null, Button? audioTracksButton = null)
	{
		_audiosFlyoutButton = audioTracksButton;
		_mp.Config.Player.KeyBindings.RemoveAll();
		if (host is not null)
		{
			host.Player = _mp;
		}

		_mp.WhenAnyValue(x => x.Status).Subscribe(status =>
		{
			if (status is Status.Ended)
			{
				_isPlaying = false;
			}
		});

		// shared, so that the position held is dropped once whoever listens has seen the player at it
		_positionChanged = Observable.Merge(
				_mp.WhenAnyValue(x => x.CurTime).Where(_ => CanEndSeek).Select(x => new TimeSpan(x)).Where(ShouldReportPosition),
				_seekPosition,
				_seek.Reminders(TimeSpan.FromMilliseconds(250)))
			.Publish()
			.RefCount();
		_mp.PlaybackStopped += (_, e) =>
		{
			if (!_holding)
			{
				LastError = e.Error?.ToString() ?? LastError;
			}
		};

		if (_audiosFlyoutButton is not null)
		{
			_mp.Audio.Streams
			   .ToObservableChangeSet()
			   .Throttle(TimeSpan.FromSeconds(1))
			   .ObserveOn(RxSchedulers.MainThreadScheduler)
			   .Subscribe(_ =>
			   {
				   _audiosFlyoutButton.Flyout = Converters.Converters.GetAudiosFlyout(this, _defaultAudioIndex);
				   ApplyPendingAudioTrack();
			   });
		}
	}

	private bool CanEndSeek => !_holding && _opened;

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

	public string? LastError { get; private set; }

	public MediaPlayerState State => ConvertState(_mp.Status);

	public TimeSpan Position
	{
		get
		{
			// read when the app leaves the item and reports the position: it must not depend on anyone listening to the position events
			var actual = new TimeSpan(_mp.CurTime);
			return CanEndSeek && ShouldReportPosition(actual) ? actual : _seek.Target ?? actual;
		}
	}

	public bool IsPlaying => _isPlaying;
	public bool IsMuted => _mp.Audio.Mute;
	public int? SubtitleTrackIndex => _mp.Subtitles.Streams.FirstOrDefault(x => x.Enabled)?.StreamIndex - 1;
	public int? AudioTrackIndex => _mp.Audio.Streams.FirstOrDefault(x => x.Enabled)?.StreamIndex - 1;

	public int SubtitleTrackCount => _mp.Subtitles.Streams.Count;

	public double Volume
	{
		get => _mp.Audio.Volume;
		set => _mp.Audio.Volume = (int)value;
	}

	public IObservable<TimeSpan> DurationChanged => _mp.WhenAnyValue(x => x.Duration).Select(x => new TimeSpan(x)).Where(d => d > TimeSpan.Zero);
	public IObservable<TimeSpan> PositionChanged => _positionChanged;
	public IObservable<Unit> Playing => _playing;
	public IObservable<Unit> Paused => _paused;
	public IObservable<Unit> Ended => _mp.WhenAnyValue(x => x.Status).Where(x => x is Status.Ended).Select(_ => Unit.Default);
	public IObservable<Unit> Errored => _mp.WhenAnyValue(x => x.Status).Where(x => !_holding && x is Status.Failed).Select(_ => Unit.Default);
	public IObservable<Unit> Stopped => _mp.WhenAnyValue(x => x.Status).Where(x => x is Status.Stopped).Select(_ => Unit.Default);
	public IObservable<double> VolumeChanged => _mp.WhenAnyValue(x => x.Audio.Volume).Select(x => x * 1d);
	public IObservable<string> SubtitleText => _mp.WhenAnyValue(x => x.Subtitles.SubsText);
	public IObservable<Unit> MediaLoaded { get; } = Observable.Empty<Unit>();

	public void Dispose()
	{
		Stop();
		_mp.Dispose();
	}

	public SourceAudioMenu? SourceAudioMenu { get; set; }

	public IEnumerable<AudioTrack> GetAudioTracks() => _mp.Audio.Streams.Select(x => new AudioTrack(x.StreamIndex, x.Language.TopEnglishName, x.Title));
	public void OpenAudioTrack(int index)
	{
		if (_mp.Audio.Streams.FirstOrDefault(x => x.StreamIndex == index) is { } stream)
		{
			_mp.Open(stream);
		}
	}

	public void OpenExternalSubtitleTrack(string url)
	{
		_mp.Config.Subtitles.Enabled = true;
		_mp.Open(url, forceSubtitles: true);
	}
	
	public void OpenInternalSubtitleTrack(int trackIndex, int subtitleIndex)
	{
		_mp.Config.Subtitles.Enabled = true;
		_mp.Open(_mp.Subtitles.Streams[subtitleIndex]);
	}

	public void DisableSubtitles() => _mp.Config.Subtitles.Enabled = false;

	public void Pause()
	{
		if (_holding)
		{
			_keepIntentOnOpen = true;
		}

		SetIsPlaying(false);
		_mp.Config.Player.AutoPlay = false;
		if (_hasMedia)
		{
			_mp.Pause();
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

		if (!_hasMedia)
		{
			return false;
		}

		SetIsPlaying(true);
		_mp.Config.Player.AutoPlay = true;
		_mp.Play();
		if (_seek.Target is { } target && target > TimeSpan.Zero && _lastSeekIssued != target)
		{
			_lastSeekIssued = target;
			_mp.SeekAccurate((int)target.TotalMilliseconds);
		}

		return true;
	}

	public bool Play(Uri uri, int defaultAudioIndex = 0, int? audioTrackOrdinal = null)
	{
		_defaultAudioIndex = defaultAudioIndex;
		_pendingAudioOrdinal = audioTrackOrdinal;
		_holding = false;
		_opened = false;
		_lastSeekIssued = null;
		if (_keepIntentOnOpen)
		{
			_keepIntentOnOpen = false;
		}
		else
		{
			SetIsPlaying(true);
		}

		LastError = null;
		_mp.Config.Player.AutoPlay = _isPlaying;
		var args = _mp.Open(HttpUtility.UrlDecode(uri.ToString()));
		if (!args.Success)
		{
			_hasMedia = false;
			LastError = "Flyleaf could not open the media";
			return false;
		}

		_hasMedia = true;
		if (_audiosFlyoutButton is null)
		{
			ApplyPendingAudioTrack();
		}

		if (_seek.Target is { } target && target > TimeSpan.Zero && _isPlaying)
		{
			_lastSeekIssued = target;
			_mp.SeekAccurate((int)target.TotalMilliseconds);
		}
		else if (!_isPlaying && _mp.IsPlaying)
		{
			_mp.Pause();
		}

		_opened = true;
		return true;
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

	public void SeekTo(TimeSpan timeSpan)
	{
		_holding = false;
		_seek.Set(timeSpan);
		_seekPosition.OnNext(timeSpan);
		if (_hasMedia && _isPlaying && _lastSeekIssued != timeSpan)
		{
			_lastSeekIssued = timeSpan;
			_mp.SeekAccurate((int)timeSpan.TotalMilliseconds);
		}
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
		_lastSeekIssued = null;
		_keepIntentOnOpen = _hasMedia || _keepIntentOnOpen;
		if (held > TimeSpan.Zero)
		{
			_seek.Set(held);
			_seekPosition.OnNext(held);
		}
		else
		{
			_seek.Clear();
		}

		if (_mp.IsPlaying)
		{
			_mp.Pause();
		}
	}

	public void Stop()
	{
		_holding = false;
		_keepIntentOnOpen = false;
		_opened = false;
		_hasMedia = false;
		_isPlaying = false;
		_lastSeekIssued = null;
		_mp.Stop();
	}

	private static MediaPlayerState ConvertState(Status status)
	{
		return status switch
		{
			Status.Opening => MediaPlayerState.Opening,
			Status.Playing => MediaPlayerState.Playing,
			Status.Paused => MediaPlayerState.Paused,
			Status.Stopped => MediaPlayerState.Stopped,
			Status.Ended => MediaPlayerState.Ended,
			Status.Failed => MediaPlayerState.Error,
			_ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
		};
	}
}
