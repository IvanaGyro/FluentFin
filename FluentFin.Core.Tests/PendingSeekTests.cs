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
}
