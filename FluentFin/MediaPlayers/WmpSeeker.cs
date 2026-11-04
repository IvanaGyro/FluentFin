using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Core.Services;
using ReactiveMarbles.ObservableEvents;
using Windows.Media.Playback;

namespace FluentFin.MediaPlayers;

/// <summary>
/// The position of Windows Media Player, with the seek the app asks for right after opening the media. The player cannot seek
/// before the media is open, it starts a stream (a transcode starts at zero) and reports zero, which resets the time bar for a few
/// seconds. The seek is kept, shown at once and made when the player can do it; what the player reports before it is there is not shown.
/// </summary>
internal sealed class WmpSeeker
{
	private readonly MediaPlayer _mp;
	private readonly PendingSeek _seek = new();
	private readonly Subject<TimeSpan> _shown = new();

	private readonly Subject<bool> _videoVisible = new();
	private readonly object _gate = new();
	private volatile bool _opened;
	private volatile bool _holding;
	private volatile bool _buffering;
	private volatile bool _seekRequested;
	private volatile bool _seekCompleted;
	private TimeSpan? _seekTarget;
	private DateTimeOffset _loadingSince;

	/// <summary>How long the video is kept hidden at most: it is shown even if the player never gets where the app wants it.</summary>
	private static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(75);

	public WmpSeeker(MediaPlayer mp)
	{
		_mp = mp;
		_mp.PlaybackSession.PlaybackStateChanged += (_, _) => EvaluateVideo();

		// The player reports the position that was asked for, and the state Playing, while it still buffers at the new position and
		// shows the frame it had (the start of the media). The seek is done, and the buffer filled, only when it says so.
		_mp.PlaybackSession.SeekCompleted += (_, _) =>
		{
			lock (_gate)
			{
				_seekCompleted = true;
			}

			EvaluateVideo();
		};
		_mp.PlaybackSession.BufferingStarted += (_, _) =>
		{
			lock (_gate)
			{
				_buffering = true;
			}
		};
		_mp.PlaybackSession.BufferingEnded += (_, _) =>
		{
			lock (_gate)
			{
				_buffering = false;
			}

			EvaluateVideo();
		};
		_mp.PlaybackSession.PositionChanged += (_, _) =>
		{
			ApplyPendingSeek(ActualPosition);
			EvaluateVideo();
		};
		PositionChanged = Observable.Merge(
				_mp.PlaybackSession.Events().PositionChanged.Where(_ => CanEndSeek).Select(_ => ActualPosition).Where(_seek.ShouldReport),
				_shown,
				_seek.Reminders(TimeSpan.FromMilliseconds(250)))
			.Publish()
			.RefCount();
	}

	/// <summary>
	/// Whether the video is to be shown. The player shows the first frame of the media (the start of the file or of the stream) as soon
	/// as it opens it, before the seek to where the item was is made, and the picture flashes back to the start. The video stays hidden
	/// (black) from the moment the media is loaded until the player is at the position the item resumes at, and playing.
	/// </summary>
	public bool IsVideoVisible { get; private set; } = true;

	public IObservable<bool> VideoVisibleChanged => _videoVisible;

	public TimeSpan? Target => _seek.Target;

	/// <summary>New media is being loaded: its video is hidden until it is where it has to be.</summary>
	public void BeginLoading()
	{
		lock (_gate)
		{
			_holding = false;
			_opened = false;
			_buffering = false;
			_seekTarget = _seek.Target;
			_seekRequested = _seekTarget is not null;
			_seekCompleted = false;
			_loadingSince = DateTimeOffset.UtcNow;
		}

		SetVideoVisible(false);
	}

	private void SetVideoVisible(bool visible)
	{
		lock (_gate)
		{
			if (IsVideoVisible == visible)
			{
				return;
			}

			IsVideoVisible = visible;
			if (visible)
			{
				_seekTarget = null;
			}
		}

		_videoVisible.OnNext(visible);
	}

	private void EvaluateVideo()
	{
		if (IsVideoVisible)
		{
			return;
		}

		if (DateTimeOffset.UtcNow - _loadingSince > LoadingTimeout)
		{
			SetVideoVisible(true);
			return;
		}

		var actual = ActualPosition;
		var state = _mp.SafeGetValue(mp => mp.PlaybackSession.PlaybackState, MediaPlaybackState.None);
		lock (_gate)
		{
			if (IsVideoVisible || _holding)
			{
				return;
			}

			var arrived = _seekTarget is not { } target || (actual - target).Duration() <= PendingSeek.Tolerance;
			var playing = PlayRequested
				? state == MediaPlaybackState.Playing
				: state is MediaPlaybackState.Playing or MediaPlaybackState.Paused;
			var seeked = !_seekRequested || _seekCompleted;
			if (!_opened || !arrived || !playing || _buffering || !seeked)
			{
				return;
			}
		}

		_seek.ShouldReport(actual);
		SetVideoVisible(true);
		_shown.OnNext(actual);
	}

	private bool CanEndSeek
	{
		get
		{
			lock (_gate)
			{
				return !_holding && _opened && (!_seekRequested || _seekCompleted);
			}
		}
	}

	/// <summary>Shared, so that the seek is made once per event whoever listens.</summary>
	public IObservable<TimeSpan> PositionChanged { get; }

	/// <summary>
	/// The position to show: the one asked for until the player has reached it. It is read when the app leaves the item and reports the
	/// position to the server, so it must not depend on anyone listening to the position events to learn that the player is there.
	/// </summary>
	public TimeSpan Position
	{
		get
		{
			var actual = ActualPosition;
			return CanEndSeek && _seek.ShouldReport(actual) ? actual : _seek.Target ?? actual;
		}
	}

	/// <summary>Shows the position until the player is at it, null ends it.</summary>
	public void Hold(TimeSpan? position)
	{
		if (position is not { } held)
		{
			lock (_gate)
			{
				_holding = false;
				_seekTarget = null;
				_seekRequested = false;
			}

			_seek.Clear();
			return;
		}

		lock (_gate)
		{
			_holding = true;
			_opened = false;
			_buffering = false;
			_seekTarget = held > TimeSpan.Zero ? held : null;
			_seekRequested = held > TimeSpan.Zero;
			_seekCompleted = false;
			_loadingSince = DateTimeOffset.UtcNow;
		}

		if (held > TimeSpan.Zero)
		{
			_seek.Set(held);
			_shown.OnNext(held);
		}
		else
		{
			_seek.Clear();
		}

		SetVideoVisible(false);
		try
		{
			_mp.Pause();
		}
		catch
		{
		}
	}

	public void SeekTo(TimeSpan time)
	{
		// the start is the earliest a seek can go: a target before it is never reached and would hold the time bar there
		if (time < TimeSpan.Zero)
		{
			time = TimeSpan.Zero;
		}

		bool opened;
		lock (_gate)
		{
			_holding = false;
			opened = _opened;
		}

		// The media is open, it can seek now, or it buffers (after a seek to a part the server has not sent yet) and does not open again:
		// the seek is not deferred to the opening and the player is not paused for it.
		var canSeek = CanSeekNow;
		if (canSeek || opened)
		{
			if (!IsVideoVisible)
			{
				lock (_gate)
				{
					_seekTarget = time;
					_seekRequested = true;
				}

				_seek.Set(time);
				_shown.OnNext(time);
				SetPosition(time);
				return;
			}

			lock (_gate)
			{
				_seekTarget = null;
				_seekRequested = false;
			}

			if (canSeek)
			{
				_seek.Clear();
			}
			else
			{
				// held until the player is there, and made again by the position events if it did not take it while it buffered
				_seek.Set(time);
				_shown.OnNext(time);
			}

			SetPosition(time);
			return;
		}

		lock (_gate)
		{
			_opened = false;
			_seekTarget = time;
			_seekRequested = true;
		}

		_seek.Set(time);
		_shown.OnNext(time);

		// Setting the position while the media opens makes the player preroll the target frame instead of the start of the stream
		// (0:00:00). It waits, paused, until the media is open and at the position, and then plays.
		SetPosition(time);
		try
		{
			_mp.Pause();
			_playWhenSeeked = PlayRequested;
			if (_playWhenSeeked)
			{
				_ = PlayAnywayLater();
			}
		}
		catch
		{
		}
	}

	/// <summary>The media is open, the player can seek now if it has not already seeked while opening.</summary>
	public void NotifyOpened()
	{
		if (_seek.Target is { } target && (_seek.NeedsSeek(ActualPosition) || !_seekCompleted))
		{
			SetPosition(target);
		}

		PlayIfWaiting();
		lock (_gate)
		{
			_holding = false;
			_opened = true;
		}

		EvaluateVideo();
	}

	/// <summary>
	/// The media is open: the position is set first and then <paramref name="afterPosition"/> runs (the audio track of the item is
	/// selected). A track selected before the position restarts the playback from the start of the file and the item does not resume
	/// where it was (WmpSeekTests).
	/// </summary>
	public void OnMediaOpened(Action afterPosition)
	{
		NotifyOpened();
		afterPosition();
	}

	/// <summary>The app plays or pauses by itself, the player does not play on its own once it is at the position.</summary>
	public void CancelPlayWhenSeeked() => _playWhenSeeked = false;

	public void RequestPause()
	{
		PlayRequested = false;
		_playWhenSeeked = false;
		try
		{
			_mp.Pause();
		}
		catch
		{
		}
	}

	public void RequestPlay()
	{
		PlayRequested = true;
		bool waitForOpen;
		lock (_gate)
		{
			if (_holding)
			{
				return;
			}

			waitForOpen = !_opened && _seekRequested;
		}

		if (waitForOpen)
		{
			_playWhenSeeked = true;
			_ = PlayAnywayLater();
			return;
		}

		_playWhenSeeked = false;
		try
		{
			_mp.Play();
		}
		catch
		{
		}
	}

	private volatile bool _playWhenSeeked;

	/// <summary>
	/// Whether the app wants the media to play, as it asked for last. A seek that has to wait pauses the player, and then plays only
	/// if the app wanted to play: a pause of the user is kept.
	/// </summary>
	public volatile bool PlayRequested;

	private void PlayIfWaiting()
	{
		if (!_playWhenSeeked)
		{
			return;
		}

		_playWhenSeeked = false;
		try
		{
			_mp.Play();
		}
		catch
		{
		}
	}

	/// <summary>A player that never reports the media open does not stay paused.</summary>
	private async Task PlayAnywayLater()
	{
		await Task.Delay(TimeSpan.FromSeconds(10));
		PlayIfWaiting();
	}

	private TimeSpan ActualPosition => _mp.SafeGetValue(mp => mp.PlaybackSession.Position, TimeSpan.Zero);

	private bool CanSeekNow => _mp.SafeGetValue(mp => mp.PlaybackSession.CanSeek && mp.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused, false);

	private void ApplyPendingSeek(TimeSpan reported)
	{
		if (!_holding && _seek.Target is { } target && _seek.NeedsSeek(reported) && CanSeekNow)
		{
			SetPosition(target);
			PlayIfWaiting();
		}
	}

	private void SetPosition(TimeSpan time)
	{
		lock (_gate)
		{
			_seekCompleted = false;
		}

		try
		{
			_mp.PlaybackSession.Position = time;
		}
		catch
		{
		}
	}
}
