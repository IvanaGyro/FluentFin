using FluentFin.Core.Services;

namespace FluentFin.Core.Tests;

public sealed class PendingSeekTests
{
	[Fact]
	public void WithoutASeek_EveryPositionIsReported()
	{
		var seek = new PendingSeek();

		Assert.True(seek.ShouldReport(TimeSpan.Zero));
	}

	[Fact]
	public void TheStartOfTheNewStream_IsNotReported_UntilThePlayerIsAtTheTarget()
	{
		var seek = new PendingSeek();
		seek.Set(TimeSpan.FromMinutes(10));

		Assert.False(seek.ShouldReport(TimeSpan.Zero));
		Assert.False(seek.ShouldReport(TimeSpan.FromSeconds(3)));
		Assert.NotNull(seek.Target);

		Assert.True(seek.ShouldReport(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1)));
		Assert.Null(seek.Target);
		Assert.True(seek.ShouldReport(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1)));
	}

	[Fact]
	public void Clear_EndsTheSeek()
	{
		var seek = new PendingSeek();
		seek.Set(TimeSpan.FromMinutes(1));
		seek.Clear();

		Assert.True(seek.ShouldReport(TimeSpan.Zero));
	}

	[Fact]
	public void AResumeBeyondTheTarget_IsNotReported_AndNeedsASeekBack()
	{
		var seek = new PendingSeek();
		seek.Set(TimeSpan.FromMinutes(10));

		// the player resumed at its previous position, 20 minutes
		var resumed = TimeSpan.FromMinutes(20);
		Assert.True(seek.NeedsSeek(resumed));
		Assert.False(seek.ShouldReport(resumed));
		Assert.NotNull(seek.Target);

		Assert.False(seek.NeedsSeek(TimeSpan.FromMinutes(10)));
		Assert.True(seek.ShouldReport(TimeSpan.FromMinutes(10)));
		Assert.Null(seek.Target);
	}

	[Fact]
	public void ThePlayerIsGivenUpOn_WhenItNeverReachesTheTarget()
	{
		var now = DateTimeOffset.UtcNow;
		var seek = new PendingSeek(() => now);
		seek.Set(TimeSpan.FromMinutes(10));

		Assert.False(seek.ShouldReport(TimeSpan.Zero));

		now += PendingSeek.Timeout + TimeSpan.FromSeconds(1);
		Assert.False(seek.NeedsSeek(TimeSpan.Zero));
		Assert.True(seek.ShouldReport(TimeSpan.Zero));
		Assert.Null(seek.Target);
	}

	/// <summary>Timers on a busy runner are late: wait for the condition, with room, instead of for a time.</summary>
	private static async Task<bool> WaitFor(Func<bool> condition)
	{
		var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (DateTime.UtcNow < end)
		{
			if (condition())
			{
				return true;
			}

			await Task.Delay(10);
		}

		return condition();
	}

	[Fact]
	public async Task TheHeldPosition_IsRepeated_WhileItIsHeld()
	{
		var seek = new PendingSeek();
		var seen = new List<TimeSpan>();
		using var subscription = seek.Reminders(TimeSpan.FromMilliseconds(20)).Subscribe(x => { lock (seen) { seen.Add(x); } });

		// nothing is held: no reminders
		await Task.Delay(150);
		lock (seen) { Assert.Empty(seen); }

		seek.Set(TimeSpan.FromMinutes(8));
		Assert.True(await WaitFor(() => { lock (seen) { return seen.Count >= 2; } }), "no reminders while the position is held");
		seek.Clear();

		// the timer is gone with the hold: what was in flight is delivered, then nothing
		await Task.Delay(150);
		int count;
		lock (seen) { count = seen.Count; }
		await Task.Delay(150);
		lock (seen)
		{
			Assert.Equal(count, seen.Count);
			Assert.All(seen, x => Assert.Equal(TimeSpan.FromMinutes(8), x));
		}
	}

	[Fact]
	public async Task TheReminders_StopWhenTheHeldPositionTimesOut()
	{
		var now = DateTimeOffset.UtcNow;
		var seek = new PendingSeek(() => now);
		var seen = new List<TimeSpan>();
		using var subscription = seek.Reminders(TimeSpan.FromMilliseconds(20)).Subscribe(x => { lock (seen) { seen.Add(x); } });

		seek.Set(TimeSpan.FromMinutes(8));
		Assert.True(await WaitFor(() => { lock (seen) { return seen.Count >= 1; } }), "no reminders while the position is held");

		now += PendingSeek.Timeout + TimeSpan.FromSeconds(1);
		Assert.True(await WaitFor(() => seek.Target is null), "the hold did not end");

		await Task.Delay(150);
		int count;
		lock (seen) { count = seen.Count; }
		await Task.Delay(150);
		lock (seen) { Assert.Equal(count, seen.Count); }
	}
}
