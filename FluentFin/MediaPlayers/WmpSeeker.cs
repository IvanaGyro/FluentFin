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

	public WmpSeeker(MediaPlayer mp)
	{
		_mp = mp;
		PositionChanged = Observable.Merge(
				_mp.PlaybackSession.Events().PositionChanged.Select(_ => ActualPosition).Do(ApplyPendingSeek).Where(_seek.ShouldReport),
				_shown,
				_seek.Reminders(TimeSpan.FromMilliseconds(250)))
			.Publish()
			.RefCount();
	}

	/// <summary>Shared, so that the seek is made once per event whoever listens.</summary>
	public IObservable<TimeSpan> PositionChanged { get; }

	/// <summary>The position to show: the one asked for until the player has reached it.</summary>
	public TimeSpan Position => _seek.Target ?? ActualPosition;

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
		try
		{
			_mp.PlaybackSession.Position = time;
		}
		catch
		{
		}
	}
}
