namespace FluentFin.Core.Tests;

public sealed class ApiKeyMaskingExtensionsTests
{
	[Fact]
	public void MaskApiKey_KeepsTheParameterAndMasksTheValue()
	{
		var masked = new Uri("http://server/a?x=1&ApiKey=secret&y=2").MaskApiKey();

		Assert.Equal("http://server/a?x=1&ApiKey=***&y=2", masked);
	}

	[Fact]
	public void MaskApiKey_MasksEveryOccurrenceInAnErrorMessage()
	{
		const string message = "Error main: VLC is unable to open the MRL 'http://s/m.m3u8?a=1&ApiKey=secret&b=2&ApiKey=secret'. Check the log for details.";

		var masked = message.MaskApiKey();

		Assert.DoesNotContain("secret", masked);
		Assert.Equal("Error main: VLC is unable to open the MRL 'http://s/m.m3u8?a=1&ApiKey=***&b=2&ApiKey=***'. Check the log for details.", masked);
	}

	[Theory]
	[InlineData("http://s/a?api_key=secret", "http://s/a?api_key=***")]
	[InlineData("http://s/a?APIKEY=secret", "http://s/a?APIKEY=***")]
	[InlineData("http://s/a?ApiKey=secret", "http://s/a?ApiKey=***")]
	[InlineData("http://s/a?ApiKey=secret\"", "http://s/a?ApiKey=***\"")]
	[InlineData("ApiKey=secret and more", "ApiKey=*** and more")]
	public void MaskApiKey_HandlesNamesAndDelimiters(string text, string expected)
	{
		Assert.Equal(expected, text.MaskApiKey());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("nothing to hide http://s/a?b=1")]
	public void MaskApiKey_LeavesOtherTextAlone(string? text)
	{
		Assert.Equal(text ?? "", text.MaskApiKey());
	}
}
