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
	private volatile bool _opened;
	private volatile bool _buffering;
	private volatile bool _seekRequested;
	private volatile bool _seekCompleted;
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
			_seekCompleted = true;
			EvaluateVideo();
		};
		_mp.PlaybackSession.BufferingStarted += (_, _) => _buffering = true;
		_mp.PlaybackSession.BufferingEnded += (_, _) =>
		{
			_buffering = false;
			EvaluateVideo();
		};
		PositionChanged = Observable.Merge(
				_mp.PlaybackSession.Events().PositionChanged.Select(_ => ActualPosition).Do(ApplyPendingSeek).Do(_ => EvaluateVideo()).Where(_seek.ShouldReport),
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

	/// <summary>New media is being loaded: its video is hidden until it is where it has to be.</summary>
	public void BeginLoading()
	{
		_opened = false;
		_buffering = false;
		_seekRequested = false;
		_seekCompleted = false;
		_loadingSince = DateTimeOffset.UtcNow;
		SetVideoVisible(false);
	}

	private void SetVideoVisible(bool visible)
	{
		if (IsVideoVisible == visible)
		{
			return;
		}

		IsVideoVisible = visible;
		_videoVisible.OnNext(visible);
	}

	private void EvaluateVideo()
	{
		if (IsVideoVisible)
		{
			return;
		}

		var arrived = _seek.ShouldReport(ActualPosition);
		var playing = _mp.SafeGetValue(mp => mp.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused, false);
		var seeked = !_seekRequested || _seekCompleted;
		if ((_opened && arrived && playing && !_buffering && seeked) || DateTimeOffset.UtcNow - _loadingSince > LoadingTimeout)
		{
			SetVideoVisible(true);
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
			return _seek.ShouldReport(actual) ? actual : _seek.Target ?? actual;
		}
	}

	/// <summary>Shows the position until the player is at it, null ends it.</summary>
	public void Hold(TimeSpan? position)
	{
		if (position is not { } held)
		{
			_seek.Clear();
			return;
		}

		_seek.Set(held);
		_shown.OnNext(held);
		BeginLoading();
		_seekRequested = true;
	}

	public void SeekTo(TimeSpan time)
	{
		if (CanSeekNow)
		{
			_seek.Clear();
			SetPosition(time);
			return;
		}

		_seek.Set(time);
		_seekRequested = true;
		_shown.OnNext(time);

		// The player started playing the stream from its start, and shows it, while it cannot seek yet. It waits, paused, until the
		// media is open and has been moved to the position, and then plays.
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

	/// <summary>The media is open, the player can seek now.</summary>
	public void NotifyOpened()
	{
		if (_seek.Target is { } target)
		{
			SetPosition(target);
		}

		PlayIfWaiting();
		_opened = true;
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
		if (_seek.Target is { } target && _seek.NeedsSeek(reported) && CanSeekNow)
		{
			SetPosition(target);
			PlayIfWaiting();
		}
	}

	private void SetPosition(TimeSpan time)
	{
		_seekCompleted = false;
		try
		{
			_mp.PlaybackSession.Position = time;
		}
		catch
		{
		}
	}
}
