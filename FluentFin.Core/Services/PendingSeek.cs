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
	}

	public void Clear()
	{
		lock (_lock) { _target = null; }
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
		lock (_lock)
		{
			if (_target is not { } target)
			{
				return true;
			}

			if ((reported - target).Duration() <= Tolerance || _clock() - _since > Timeout)
			{
				_target = null;
				return true;
			}

			return false;
		}
	}
}
