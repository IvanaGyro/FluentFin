using System.Text.RegularExpressions;

namespace FluentFin.Core;

public static partial class ApiKeyMaskingExtensions
{
	public const string Mask = "***";

	/// <summary>
	/// Keeps the <c>ApiKey</c> parameter in urls and in text that contains urls (player error messages repeat the url),
	/// but replaces its value so that the access token does not end up in logs.
	/// </summary>
	public static string MaskApiKey(this string? text)
		=> string.IsNullOrEmpty(text) ? text ?? "" : ApiKeyValue().Replace(text, Mask);

	public static string MaskApiKey(this Uri uri) => uri.ToString().MaskApiKey();

	[GeneratedRegex("(?<=(?:ApiKey|api_key)=)[^&\\s'\"]+", RegexOptions.IgnoreCase)]
	private static partial Regex ApiKeyValue();
}
