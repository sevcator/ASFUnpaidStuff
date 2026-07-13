using System.Text.RegularExpressions;

namespace Maxisoft.ASF.Reddit;

internal static partial class OldRedditHtmlParserRegexes {
	[GeneratedRegex(@"<time\b[^>]*\bdatetime\s*=\s*[""'](?<datetime>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
	internal static partial Regex DateTimeAttribute();

	[GeneratedRegex(@"after=(?<after>t1_[a-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	internal static partial Regex AfterParameter();
}
