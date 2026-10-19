using System.Net;
using System.Text.RegularExpressions;

namespace Dusiburg.AI.Ticket.Core.Context;

/// <summary>Testo semplice da un articolo HTML di Zammad: a capo per blocchi e interruzioni, voci di elenco, entità decodificate.</summary>
public static partial class HtmlText
{
    public static string ToPlainText(string body, string? contentType)
    {
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            return body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }

        string text = LineBreak().Replace(body, "\n");
        text = ListItem().Replace(text, "\n- ");
        text = BlockEnd().Replace(text, "\n\n");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = SpacesBeforeNewline().Replace(text, "\n");

        return ManyNewlines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</(?:p|div|ol|ul|h\d|blockquote)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex SpacesBeforeNewline();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();
}
