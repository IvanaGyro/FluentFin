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
				_shown)
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
	}

	/// <summary>The media is open, the player can seek now.</summary>
	public void NotifyOpened()
	{
		if (_seek.Target is { } target && CanSeekNow)
		{
			SetPosition(target);
		}
	}

	private TimeSpan ActualPosition => _mp.SafeGetValue(mp => mp.PlaybackSession.Position, TimeSpan.Zero);

	private bool CanSeekNow => _mp.SafeGetValue(mp => mp.PlaybackSession.CanSeek && mp.PlaybackSession.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused, false);

	private void ApplyPendingSeek(TimeSpan reported)
	{
		if (_seek.Target is { } target && _seek.NeedsSeek(reported) && CanSeekNow)
		{
			SetPosition(target);
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
