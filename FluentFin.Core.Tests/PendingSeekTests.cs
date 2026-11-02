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
}
