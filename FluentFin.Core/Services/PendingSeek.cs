namespace FluentFin.Core.Services;

/// <summary>
/// A seek that is asked for before the player can do it, as right after opening the media. Until the player has reached the
/// position, what it reports (zero, from the start of the new stream) is not shown, as it would reset the time bar for a few seconds.
/// </summary>
public sealed class PendingSeek
{
	private readonly object _lock = new();
	private TimeSpan? _target;

	/// <summary>How close to the target the player has to report to be taken as arrived.</summary>
	public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

	public TimeSpan? Target
	{
		get { lock (_lock) { return _target; } }
	}

	public void Set(TimeSpan target)
	{
		lock (_lock) { _target = target; }
	}

	public void Clear()
	{
		lock (_lock) { _target = null; }
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

			if (reported >= target - Tolerance)
			{
				_target = null;
				return true;
			}

			return false;
		}
	}
}
