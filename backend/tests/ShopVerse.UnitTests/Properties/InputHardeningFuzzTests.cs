using AngleSharp.Html.Parser;
using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Exceptions;
using static ShopVerse.UnitTests.Properties.PropertyRunner;

namespace ShopVerse.UnitTests.Properties;

/// <summary>
/// Adversarial inputs for the write-side defences: rich-text sanitiser (stored XSS), slugs and URLs.
/// The sanitiser output is re-parsed as a browser would, then checked against the allow-list.
/// </summary>
public class InputHardeningFuzzTests
{
    private static readonly HashSet<string> AllowedTags =
        ["p", "br", "h2", "h3", "h4", "ul", "ol", "li", "strong", "b", "em", "i", "u", "table", "thead", "tbody", "tr", "th", "td", "a", "span", "sup", "sub", "code"];

    private static readonly HashSet<string> AllowedAttributes = ["href", "title", "colspan", "rowspan"];

    // Payloads drawn from the OWASP XSS Filter Evasion Cheat Sheet and common polyglots.
    public static TheoryData<string> XssPayloads => new()
    {
        "<script>alert(1)</script>",
        "<SCRIPT SRC=//evil.example/x.js></SCRIPT>",
        "<img src=x onerror=alert(1)>",
        "<IMG SRC=\"jav&#x09;ascript:alert('XSS');\">",
        "<IMG SRC=&#106;&#97;&#118;&#97;&#115;&#99;&#114;&#105;&#112;&#116;&#58;&#97;&#108;&#101;&#114;&#116;&#40;&#39;&#88;&#83;&#83;&#39;&#41;>",
        "<svg onload=alert(1)>",
        "<svg><script>alert(1)</script></svg>",
        "<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>",
        "<a href=\"javascript:alert(1)\">x</a>",
        "<a href=\"JaVaScRiPt:alert(1)\">x</a>",
        "<a href=\" javascript:alert(1)\">x</a>",
        "<a href=\"java\tscript:alert(1)\">x</a>",
        "<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>",
        "<a href=\"vbscript:msgbox(1)\">x</a>",
        "<a href=\"https://ok.example\" onclick=\"alert(1)\">x</a>",
        "<a href=\"https://ok.example\" style=\"background:url(javascript:alert(1))\">x</a>",
        "<iframe src=\"https://evil.example\"></iframe>",
        "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>",
        "<object data=\"javascript:alert(1)\"></object>",
        "<embed src=\"javascript:alert(1)\">",
        "<form action=\"https://evil.example\"><input name=p></form>",
        "<base href=\"https://evil.example/\">",
        "<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\">",
        "<link rel=stylesheet href=\"https://evil.example/x.css\">",
        "<style>@import 'https://evil.example/x.css';</style>",
        "<div style=\"width:expression(alert(1))\">x</div>",
        "<p onmouseover=alert(1)>hover</p>",
        "<details open ontoggle=alert(1)>",
        "<body onload=alert(1)>",
        "<video><source onerror=alert(1)></video>",
        "<audio src=x onerror=alert(1)>",
        "<marquee onstart=alert(1)>x</marquee>",
        "<table background=\"javascript:alert(1)\"><tr><td>x</td></tr></table>",
        "<td background=\"javascript:alert(1)\">x</td>",
        "<span data-x=\"1\" aria-label=\"y\" id=\"z\" class=\"c\">x</span>",
        "<p>ok</p><!--<script>alert(1)</script>-->",
        "<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">",
        "<template><img src=x onerror=alert(1)></template>",
        "<a href=\"https://ok.example\"><img src=x onerror=alert(1)></a>",
        "jaVasCript:/*-/*`/*\\`/*'/*\"/**/(/* */oNcliCk=alert() )//%0D%0A%0d%0a//</stYle/</titLe/</teXtarEa/</scRipt/--!>\\x3csVg/<sVg/oNloAd=alert()//>\\x3e",
        "<<script>script>alert(1)<</script>/script>",
        "<scr<script>ipt>alert(1)</scr</script>ipt>",
        "\"><script>alert(1)</script>",
        "<a href=\"&#x6A;avascript:alert(1)\">x</a>",
        "<a href=\"javascript&colon;alert(1)\">x</a>",
        "<a href=\"//evil.example\">x</a>",
        "<a href=\"\\\\evil.example\">x</a>",
    };

    private static void AssertSafe(string input)
    {
        var output = RichTextSanitizer.Sanitize(input);
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + output + "</body></html>");

        foreach (var el in doc.Body!.QuerySelectorAll("*"))
        {
            var tag = el.LocalName;
            AllowedTags.Should().Contain(tag, "tag <{0}> survived sanitising '{1}' → '{2}'", tag, input, output);
            foreach (var attr in el.Attributes)
            {
                AllowedAttributes.Should().Contain(attr.Name, "attribute {0} survived sanitising '{1}' → '{2}'", attr.Name, input, output);
                if (attr.Name == "href")
                {
                    var href = attr.Value.Trim();
                    var lower = href.ToLowerInvariant();
                    var ok = lower.StartsWith("https://", StringComparison.Ordinal) || lower.StartsWith("mailto:", StringComparison.Ordinal)
                        || lower.StartsWith("tel:", StringComparison.Ordinal)
                        || (!lower.Contains(':') && !lower.StartsWith("//", StringComparison.Ordinal) && !lower.StartsWith("\\\\", StringComparison.Ordinal));
                    ok.Should().BeTrue("href '{0}' is not an allowed scheme (input '{1}')", href, input);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(XssPayloads))]
    public void Sanitiser_NeutralisesKnownXssPayloads(string payload) => AssertSafe(payload);

    [Fact]
    public void Sanitiser_NeutralisesRandomlyComposedMarkup()
    {
        string[] tags = ["script", "img", "svg", "a", "p", "iframe", "style", "table", "td", "math", "span", "object", "x-y", "SCRIPT", "ImG"];
        string[] attrs = ["onerror", "onload", "href", "src", "style", "title", "srcdoc", "formaction", "xlink:href", "data-x", "colspan", "OnClIcK"];
        string[] values = ["javascript:alert(1)", "https://ok.example", "x", "data:text/html,<script>alert(1)</script>", "&#106;avascript:alert(1)", "\" onmouseover=\"alert(1)", "//evil", "mailto:a@b.c"];

        ForAll(301, 3_000, (rng, _) =>
        {
            var sb = new System.Text.StringBuilder();
            var depth = rng.Next(1, 6);
            var open = new Stack<string>();
            for (var d = 0; d < depth; d++)
            {
                var t = tags[rng.Next(tags.Length)];
                sb.Append('<').Append(t);
                for (var k = rng.Next(0, 3); k > 0; k--)
                    sb.Append(' ').Append(attrs[rng.Next(attrs.Length)]).Append("=\"").Append(values[rng.Next(values.Length)]).Append('"');
                sb.Append('>').Append(rng.NastyString(10));
                open.Push(t);
            }
            while (open.Count > 0 && rng.Next(3) > 0) sb.Append("</").Append(open.Pop()).Append('>');
            AssertSafe(sb.ToString());
        });
    }

    [Fact]
    public void Slug_FromAnyInput_EitherRejectsCleanlyOrProducesAValidIdempotentSlug()
    {
        ForAll(401, 20_000, (rng, _) =>
        {
            var input = rng.Next(10) == 0 ? new string('a', rng.Next(250, 600)) + rng.NastyString(20) : rng.NastyString(120);
            string slug;
            try
            {
                slug = Slug.From(input);
            }
            catch (ValidationException)
            {
                return; // clean rejection is fine; any other exception type would surface as an HTTP 500
            }

            Slug.IsValid(slug).Should().BeTrue("'{0}' produced invalid slug '{1}'", input, slug);
            slug.Length.Should().BeLessThanOrEqualTo(Slug.MaxLength);
            Slug.From(slug).Should().Be(slug, "slug generation must be idempotent");
        });
    }

    [Fact]
    public void Slug_IsValid_NeverAcceptsTraversalOrEncodedSeparators()
    {
        foreach (var s in new[] { "..", "../etc", "a/b", "a%2fb", "a..b", "-a", "a-", "a--b", "A", "a b", "a\u0000", "ä", "a\\b", "" })
            Slug.IsValid(s).Should().BeFalse(s);
    }

    [Fact]
    public void SafeUrl_RandomStrings_OnlyHttpsOrRootRelativeSurvive()
    {
        string[] prefixes = ["", "https://", "http://", "javascript:", "JAVASCRIPT:", " javascript:", "\u0000javascript:", "data:", "vbscript:", "//", "/", "/\\", "\\\\", "https:", "file://", "ftp://", "blob:", "mailto:"];
        ForAll(501, 20_000, (rng, _) =>
        {
            var url = prefixes[rng.Next(prefixes.Length)] + rng.NastyString(30);
            if (!SafeUrl.IsSafe(url)) return;

            url.Any(char.IsControl).Should().BeFalse();
            url.Any(char.IsWhiteSpace).Should().BeFalse();
            url.Contains('\\').Should().BeFalse();
            if (url.StartsWith('/'))
            {
                url.StartsWith("//", StringComparison.Ordinal).Should().BeFalse();
            }
            else
            {
                Uri.TryCreate(url, UriKind.Absolute, out var uri).Should().BeTrue();
                uri!.Scheme.Should().Be("https", "only https absolute URLs may pass, got '{0}'", url);
            }
        });
    }
}
