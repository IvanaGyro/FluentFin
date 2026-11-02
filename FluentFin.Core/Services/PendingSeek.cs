using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace FluentFin.Core.Services;

/// <summary>
/// A seek that is asked for before the player can do it, as right after opening the media. Until the player has reached the
/// position, what it reports (zero, from the start of the new stream, or the position it resumed at) is not shown, as it would
/// reset the time bar for a few seconds.
/// </summary>
public sealed class PendingSeek(Func<DateTimeOffset>? clock = null)
{
	private readonly object _lock = new();
	private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
	private readonly Subject<TimeSpan?> _targetChanged = new();
	private TimeSpan? _target;
	private DateTimeOffset _since;

	/// <summary>How close to the target the player has to report to be taken as arrived.</summary>
	public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

	/// <summary>How long the player gets to reach the target, after that it reports what it has, the time bar must not stay stuck.</summary>
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

	public TimeSpan? Target
	{
		get { lock (_lock) { return _target; } }
	}

	public void Set(TimeSpan target)
	{
		lock (_lock)
		{
			_target = target;
			_since = _clock();
		}

		_targetChanged.OnNext(target);
	}

	public void Clear()
	{
		bool had;
		lock (_lock)
		{
			had = _target is not null;
			_target = null;
		}

		if (had)
		{
			_targetChanged.OnNext(null);
		}
	}

	/// <summary>
	/// The target again and again while there is one, and nothing (no timer) while there is none or once it timed out. A time bar that was
	/// given the position before it knew the length of the new media holds the value clamped to the old length (zero), and the player
	/// gives it nothing new while the position is held.
	/// </summary>
	public IObservable<TimeSpan> Reminders(TimeSpan period) =>
		Observable.Defer(() => _targetChanged.StartWith(Target))
			.Select(target => target is null
				? Observable.Never<TimeSpan>()
				: Observable.Interval(period).Select(_ => Remind()).Where(x => x is not null).Select(x => x!.Value))
			.Switch();

	private TimeSpan? Remind()
	{
		bool timedOut;
		TimeSpan? target;
		lock (_lock)
		{
			target = _target;
			timedOut = target is not null && _clock() - _since > Timeout;
		}

		if (timedOut)
		{
			Clear();
			return null;
		}

		return target;
	}

	/// <summary>Whether the player is off the target, in either direction, and has to be asked to seek again.</summary>
	public bool NeedsSeek(TimeSpan reported)
	{
		lock (_lock)
		{
			return _target is { } target && (reported - target).Duration() > Tolerance && _clock() - _since <= Timeout;
		}
	}

	/// <summary>Whether the position reported by the player is to be used. It is once the player is at the target, which ends the seek.</summary>
	public bool ShouldReport(TimeSpan reported)
	{
		var arrived = false;
		lock (_lock)
		{
			if (_target is not { } target)
			{
				return true;
			}

			if ((reported - target).Duration() > Tolerance && _clock() - _since <= Timeout)
			{
				return false;
			}

			_target = null;
			arrived = true;
		}

		if (arrived)
		{
			_targetChanged.OnNext(null);
		}

		return true;
	}
}
