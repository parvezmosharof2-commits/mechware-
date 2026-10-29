using System.Net;
using System.Text;

namespace ShopVerse.Infrastructure.Seed;

/// <summary>
/// Generates clean, deterministic SVG artwork (logos, banners, category tiles, product renders) so the demo
/// catalogue is visually complete without hot-linking third-party photos. These are illustrations, not photographs:
/// replace them with real product photography through <see cref="SeedOptions.MediaBaseUrl"/> / the media manifest.
/// </summary>
internal static class SeedMedia
{
    private const string Bg = "#F8FAFC";

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    private static string Wrap(int w, int h, string body) =>
        $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img">{body}</svg>""";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    public static string BrandLogo(string name, string initials, string color) => Wrap(480, 240, $"""
        <rect width="480" height="240" rx="24" fill="#FFFFFF"/>
        <rect x="32" y="56" width="128" height="128" rx="28" fill="{color}"/>
        <text x="96" y="140" font-family="Inter,Arial,sans-serif" font-size="56" font-weight="700" fill="#FFFFFF" text-anchor="middle">{Esc(initials)}</text>
        <text x="184" y="128" font-family="Inter,Arial,sans-serif" font-size="{(name.Length > 14 ? 32 : 40)}" font-weight="700" fill="#0F172A">{Esc(name)}</text>
        <rect x="184" y="146" width="64" height="6" rx="3" fill="{color}"/>
        """);

    public static string SellerLogo(string name, string initials, string color) => Wrap(400, 400, $"""
        <rect width="400" height="400" rx="72" fill="{color}"/>
        <circle cx="300" cy="100" r="140" fill="#FFFFFF" opacity="0.08"/>
        <text x="200" y="236" font-family="Inter,Arial,sans-serif" font-size="150" font-weight="700" fill="#FFFFFF" text-anchor="middle">{Esc(initials)}</text>
        """);

    public static string SellerBanner(string name, string city, string color) => Wrap(1600, 400, $"""
        <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{color}"/><stop offset="1" stop-color="#0F172A"/></linearGradient></defs>
        <rect width="1600" height="400" fill="url(#g)"/>
        <g opacity="0.12" stroke="#FFFFFF" stroke-width="2" fill="none"><path d="M0 320 H400 L440 280 H900 L940 320 H1600"/><path d="M0 250 H300 L340 210 H1100 L1140 250 H1600"/><circle cx="1360" cy="120" r="70"/><circle cx="1360" cy="120" r="34"/></g>
        <text x="80" y="220" font-family="Inter,Arial,sans-serif" font-size="68" font-weight="700" fill="#FFFFFF">{Esc(Shorten(name, 34))}</text>
        <text x="80" y="280" font-family="Inter,Arial,sans-serif" font-size="30" fill="#FFFFFF" opacity="0.85">{Esc(city)}, Bangladesh · Authorised industrial supplier</text>
        """);

    public static string CategoryTile(string name, string tagline, string color, int depth) => Wrap(1200, 675, $"""
        <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{color}"/><stop offset="1" stop-color="#0B1220"/></linearGradient></defs>
        <rect width="1200" height="675" fill="url(#g)"/>
        <g opacity="0.10" stroke="#FFFFFF" stroke-width="3" fill="none">
          <rect x="740" y="120" width="360" height="260" rx="18"/><rect x="780" y="160" width="140" height="80" rx="8"/>
          <circle cx="1010" cy="200" r="26"/><circle cx="1010" cy="200" r="10"/>
          <path d="M740 440 H1100 M740 480 H1040 M740 520 H1080"/>
        </g>
        <text x="72" y="{(depth == 0 ? 330 : 360)}" font-family="Inter,Arial,sans-serif" font-size="{(depth == 0 ? 76 : 64)}" font-weight="700" fill="#FFFFFF">{Esc(Shorten(name, depth == 0 ? 26 : 30))}</text>
        <text x="72" y="{(depth == 0 ? 400 : 420)}" font-family="Inter,Arial,sans-serif" font-size="32" fill="#FFFFFF" opacity="0.8">{Esc(Shorten(tagline, 62))}</text>
        """);

    /// <summary>Square 1200×1200 product illustration on a #F8FAFC background (design system §2.6).</summary>
    public static string ProductImage(Shape shape, string brand, string model, string color, int view, int variantHue)
    {
        var sb = new StringBuilder();
        sb.Append($"""<rect width="1200" height="1200" fill="{Bg}"/>""");
        sb.Append("""<ellipse cx="600" cy="1010" rx="360" ry="34" fill="#0F172A" opacity="0.08"/>""");

        var tilt = view switch { 1 => -8, 2 => 0, 3 => 0, 4 => 0, 5 => 6, _ => 0 };
        var scale = view switch { 2 => 1.18, 3 => 0.9, 4 => 1.0, _ => 1.0 };
        sb.Append($"""<g transform="translate(600 560) rotate({tilt}) scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}) translate(-600 -560)">""");

        if (view == 3) sb.Append(Box(color, brand, model));
        else if (view == 4) sb.Append(Label(brand, model, color));
        else sb.Append(Device(shape, color, brand, model, variantHue));

        sb.Append("</g>");
        var caption = view switch { 0 => "Front view", 1 => "Angled view", 2 => "Detail", 3 => "Packaging", 4 => "Rating label", 5 => "Side view", _ => "View" };
        sb.Append($"""<text x="60" y="1140" font-family="Inter,Arial,sans-serif" font-size="30" fill="#64748B">{Esc(Shorten(brand + " " + model, 54))} · {caption}</text>""");
        return Wrap(1200, 1200, sb.ToString());
    }

    private static string Device(Shape shape, string color, string brand, string model, int hue)
    {
        var body = hue % 2 == 0 ? "#E2E8F0" : "#CBD5E1";
        var label = Esc(Shorten(model, 26));
        var brandText = Esc(brand);
        return shape switch
        {
            Shape.DinModule => $"""
                <rect x="380" y="260" width="440" height="620" rx="26" fill="{body}" stroke="#94A3B8" stroke-width="4"/>
                <rect x="380" y="260" width="440" height="96" rx="26" fill="{color}"/>
                <text x="600" y="322" font-family="Inter,Arial,sans-serif" font-size="46" font-weight="700" fill="#FFFFFF" text-anchor="middle">{brandText}</text>
                <rect x="430" y="400" width="340" height="150" rx="14" fill="#0F172A"/>
                <circle cx="470" cy="440" r="9" fill="#22C55E"/><circle cx="505" cy="440" r="9" fill="#F59E0B"/><circle cx="540" cy="440" r="9" fill="#EF4444"/>
                <text x="600" y="520" font-family="'JetBrains Mono',monospace" font-size="34" fill="#94A3B8" text-anchor="middle">{label}</text>
                {Terminals(430, 610, 7)}{Terminals(430, 760, 7)}
                """,
            Shape.Panel => $"""
                <rect x="250" y="300" width="700" height="520" rx="34" fill="#1E293B" stroke="#0F172A" stroke-width="6"/>
                <rect x="296" y="346" width="608" height="380" rx="14" fill="#0B1220"/>
                <rect x="320" y="372" width="560" height="54" rx="8" fill="{color}"/>
                <text x="344" y="412" font-family="Inter,Arial,sans-serif" font-size="30" font-weight="700" fill="#FFFFFF">{label}</text>
                <g fill="none" stroke="#38BDF8" stroke-width="5"><polyline points="340,640 440,560 520,600 620,500 720,560 860,470"/></g>
                <rect x="320" y="460" width="150" height="60" rx="8" fill="#16A34A"/><rect x="490" y="460" width="150" height="60" rx="8" fill="#F59E0B"/>
                <text x="600" y="780" font-family="Inter,Arial,sans-serif" font-size="30" fill="#94A3B8" text-anchor="middle">{brandText}</text>
                """,
            Shape.RoundSensor => $"""
                <rect x="540" y="280" width="120" height="440" rx="14" fill="#CBD5E1" stroke="#94A3B8" stroke-width="4"/>
                {Enumerable.Range(0, 9).Select(i => $"""<line x1="540" y1="{300 + i * 44}" x2="660" y2="{300 + i * 44}" stroke="#94A3B8" stroke-width="3"/>""").Aggregate("", (a, b) => a + b)}
                <rect x="520" y="200" width="160" height="90" rx="18" fill="{color}"/>
                <circle cx="600" cy="245" r="26" fill="#FDE68A"/>
                <rect x="560" y="720" width="80" height="60" fill="#475569"/>
                <path d="M600 780 C600 900 700 900 720 980" stroke="#334155" stroke-width="16" fill="none" stroke-linecap="round"/>
                <text x="600" y="360" font-family="Inter,Arial,sans-serif" font-size="28" font-weight="700" fill="#0F172A" text-anchor="middle" transform="rotate(-90 600 520)">{label}</text>
                """,
            Shape.BlockSensor => $"""
                <rect x="400" y="380" width="400" height="300" rx="28" fill="{color}" stroke="#0F172A" stroke-opacity="0.15" stroke-width="4"/>
                <rect x="430" y="410" width="340" height="120" rx="14" fill="#0F172A" opacity="0.88"/>
                <circle cx="520" cy="470" r="38" fill="#FDE68A"/><circle cx="680" cy="470" r="38" fill="#7DD3FC"/>
                <circle cx="740" cy="600" r="14" fill="#22C55E"/>
                <text x="600" y="640" font-family="Inter,Arial,sans-serif" font-size="30" font-weight="700" fill="#FFFFFF" text-anchor="middle">{label}</text>
                <rect x="560" y="680" width="80" height="60" fill="#475569"/><path d="M600 740 C600 860 680 880 700 980" stroke="#334155" stroke-width="16" fill="none" stroke-linecap="round"/>
                """,
            Shape.Cylinder => $"""
                <rect x="260" y="470" width="680" height="260" rx="30" fill="#CBD5E1" stroke="#94A3B8" stroke-width="5"/>
                <rect x="220" y="500" width="70" height="200" rx="14" fill="#64748B"/><rect x="910" y="500" width="70" height="200" rx="14" fill="#64748B"/>
                <rect x="940" y="570" width="200" height="60" rx="10" fill="#94A3B8"/><circle cx="1150" cy="600" r="30" fill="#475569"/>
                <rect x="470" y="440" width="260" height="30" fill="{color}"/><rect x="470" y="730" width="260" height="30" fill="{color}"/>
                <text x="600" y="615" font-family="Inter,Arial,sans-serif" font-size="34" font-weight="700" fill="#0F172A" text-anchor="middle">{label}</text>
                <circle cx="330" cy="470" r="14" fill="#0F172A"/><circle cx="870" cy="730" r="14" fill="#0F172A"/>
                """,
            Shape.Motor => $"""
                <rect x="250" y="440" width="560" height="330" rx="40" fill="{color}" stroke="#0F172A" stroke-opacity="0.2" stroke-width="5"/>
                {Enumerable.Range(0, 11).Select(i => $"""<rect x="{300 + i * 46}" y="440" width="18" height="330" fill="#0F172A" opacity="0.14"/>""").Aggregate("", (a, b) => a + b)}
                <rect x="810" y="500" width="120" height="210" rx="16" fill="#94A3B8"/><rect x="930" y="585" width="190" height="40" rx="8" fill="#CBD5E1" stroke="#64748B" stroke-width="4"/>
                <rect x="470" y="360" width="160" height="80" rx="12" fill="#1E293B"/>
                <rect x="300" y="770" width="100" height="60" fill="#475569"/><rect x="660" y="770" width="100" height="60" fill="#475569"/>
                <text x="530" y="620" font-family="Inter,Arial,sans-serif" font-size="34" font-weight="700" fill="#FFFFFF" text-anchor="middle">{label}</text>
                """,
            Shape.Valve => $"""
                <rect x="340" y="500" width="520" height="220" rx="24" fill="{body}" stroke="#94A3B8" stroke-width="5"/>
                <rect x="560" y="360" width="140" height="140" rx="14" fill="#1E293B"/><circle cx="630" cy="430" r="26" fill="{color}"/>
                <rect x="280" y="560" width="70" height="60" fill="#64748B"/><rect x="850" y="560" width="70" height="60" fill="#64748B"/>
                <rect x="440" y="720" width="60" height="70" fill="#64748B"/><rect x="700" y="720" width="60" height="70" fill="#64748B"/>
                <text x="600" y="625" font-family="Inter,Arial,sans-serif" font-size="32" font-weight="700" fill="#0F172A" text-anchor="middle">{label}</text>
                """,
            Shape.Cable => $"""
                <path d="M240 780 C340 380 520 320 600 600 C660 820 840 840 960 420" stroke="#1E293B" stroke-width="44" fill="none" stroke-linecap="round"/>
                <path d="M240 780 C340 380 520 320 600 600 C660 820 840 840 960 420" stroke="{color}" stroke-width="10" fill="none" stroke-linecap="round" stroke-dasharray="40 30"/>
                <rect x="190" y="760" width="110" height="70" rx="12" fill="#94A3B8"/><rect x="900" y="360" width="110" height="70" rx="12" fill="#94A3B8"/>
                <text x="600" y="940" font-family="Inter,Arial,sans-serif" font-size="32" font-weight="700" fill="#0F172A" text-anchor="middle">{label}</text>
                """,
            Shape.Enclosure => $"""
                <rect x="330" y="200" width="540" height="760" rx="20" fill="#E2E8F0" stroke="#94A3B8" stroke-width="6"/>
                <rect x="370" y="240" width="460" height="680" rx="10" fill="#F1F5F9" stroke="#CBD5E1" stroke-width="4"/>
                <rect x="760" y="520" width="26" height="120" rx="8" fill="#475569"/>
                <rect x="400" y="270" width="200" height="50" rx="8" fill="{color}"/>
                <text x="500" y="305" font-family="Inter,Arial,sans-serif" font-size="26" font-weight="700" fill="#FFFFFF" text-anchor="middle">{brandText}</text>
                <text x="600" y="610" font-family="Inter,Arial,sans-serif" font-size="30" fill="#64748B" text-anchor="middle">{label}</text>
                """,
            Shape.Meter => $"""
                <rect x="360" y="320" width="480" height="480" rx="28" fill="#1E293B" stroke="#0F172A" stroke-width="6"/>
                <rect x="410" y="380" width="380" height="150" rx="10" fill="#0B1220"/>
                <text x="600" y="490" font-family="'JetBrains Mono',monospace" font-size="96" font-weight="700" fill="#22D3EE" text-anchor="middle">88.8</text>
                <circle cx="470" cy="640" r="32" fill="#334155"/><circle cx="600" cy="640" r="32" fill="#334155"/><circle cx="730" cy="640" r="32" fill="#334155"/>
                <rect x="410" y="720" width="380" height="34" rx="8" fill="{color}"/>
                <text x="600" y="747" font-family="Inter,Arial,sans-serif" font-size="22" font-weight="700" fill="#FFFFFF" text-anchor="middle">{label}</text>
                """,
            Shape.Pump => $"""
                <rect x="260" y="560" width="560" height="200" rx="30" fill="{color}" stroke="#0F172A" stroke-opacity="0.2" stroke-width="5"/>
                <circle cx="860" cy="610" r="160" fill="#CBD5E1" stroke="#64748B" stroke-width="8"/><circle cx="860" cy="610" r="70" fill="#94A3B8"/>
                <rect x="800" y="400" width="120" height="90" fill="#64748B"/><rect x="990" y="570" width="110" height="80" fill="#64748B"/>
                <rect x="320" y="760" width="400" height="40" fill="#475569"/>
                <text x="540" y="672" font-family="Inter,Arial,sans-serif" font-size="32" font-weight="700" fill="#FFFFFF" text-anchor="middle">{label}</text>
                """,
            Shape.Gauge => $"""
                <circle cx="600" cy="560" r="290" fill="#FFFFFF" stroke="#94A3B8" stroke-width="14"/>
                <circle cx="600" cy="560" r="250" fill="none" stroke="#E2E8F0" stroke-width="3"/>
                {Enumerable.Range(0, 11).Select(i => { var a = (-210 + i * 24) * Math.PI / 180; return $"""<line x1="{(600 + 215 * Math.Cos(a)):0}" y1="{(560 + 215 * Math.Sin(a)):0}" x2="{(600 + 245 * Math.Cos(a)):0}" y2="{(560 + 245 * Math.Sin(a)):0}" stroke="#0F172A" stroke-width="6"/>"""; }).Aggregate("", (a, b) => a + b)}
                <line x1="600" y1="560" x2="720" y2="430" stroke="{color}" stroke-width="12" stroke-linecap="round"/><circle cx="600" cy="560" r="22" fill="#0F172A"/>
                <rect x="540" y="850" width="120" height="110" fill="#94A3B8"/>
                <text x="600" y="690" font-family="Inter,Arial,sans-serif" font-size="30" font-weight="700" fill="#0F172A" text-anchor="middle">{label}</text>
                """,
            Shape.Pushbutton => $"""
                <circle cx="600" cy="560" r="300" fill="#CBD5E1" stroke="#94A3B8" stroke-width="8"/>
                <circle cx="600" cy="560" r="230" fill="{color}" stroke="#0F172A" stroke-opacity="0.2" stroke-width="6"/>
                <circle cx="600" cy="560" r="170" fill="#FFFFFF" opacity="0.14"/>
                <rect x="520" y="830" width="160" height="120" fill="#64748B"/>
                <text x="600" y="570" font-family="Inter,Arial,sans-serif" font-size="34" font-weight="700" fill="#FFFFFF" text-anchor="middle">{Esc(Shorten(model, 14))}</text>
                """,
            _ => $"""
                <rect x="470" y="380" width="260" height="320" rx="30" fill="#CBD5E1" stroke="#94A3B8" stroke-width="6"/>
                <rect x="520" y="300" width="160" height="90" rx="14" fill="{color}"/>
                <rect x="540" y="700" width="120" height="120" fill="#64748B"/>
                <text x="600" y="560" font-family="Inter,Arial,sans-serif" font-size="30" font-weight="700" fill="#0F172A" text-anchor="middle">{label}</text>
                """
        };
    }

    private static string Terminals(int x, int y, int count) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"""<rect x="{x + i * 50}" y="{y}" width="38" height="70" rx="6" fill="#475569"/><circle cx="{x + 19 + i * 50}" cy="{y + 22}" r="9" fill="#CBD5E1"/>"""));

    private static string Box(string color, string brand, string model) => $"""
        <rect x="260" y="330" width="680" height="520" rx="12" fill="#D6C3A3" stroke="#B59B72" stroke-width="5"/>
        <rect x="260" y="330" width="680" height="90" rx="12" fill="#C8B48F"/>
        <rect x="330" y="470" width="540" height="260" rx="10" fill="#FFFFFF"/>
        <rect x="330" y="470" width="18" height="260" fill="{color}"/>
        <text x="380" y="545" font-family="Inter,Arial,sans-serif" font-size="46" font-weight="700" fill="{color}">{Esc(brand)}</text>
        <text x="380" y="610" font-family="Inter,Arial,sans-serif" font-size="30" fill="#0F172A">{Esc(Shorten(model, 28))}</text>
        <g fill="#0F172A">{string.Concat(Enumerable.Range(0, 28).Select(i => $"""<rect x="{380 + i * 11}" y="650" width="{(i % 3 == 0 ? 7 : 4)}" height="55"/>"""))}</g>
        """;

    private static string Label(string brand, string model, string color) => $"""
        <rect x="240" y="350" width="720" height="460" rx="16" fill="#FFFFFF" stroke="#CBD5E1" stroke-width="5"/>
        <rect x="240" y="350" width="720" height="84" rx="16" fill="{color}"/>
        <text x="280" y="408" font-family="Inter,Arial,sans-serif" font-size="40" font-weight="700" fill="#FFFFFF">{Esc(brand)}</text>
        <text x="280" y="500" font-family="'JetBrains Mono',monospace" font-size="34" fill="#0F172A">TYPE: {Esc(Shorten(model, 24))}</text>
        <text x="280" y="560" font-family="'JetBrains Mono',monospace" font-size="30" fill="#334155">IP20 · CE · RoHS</text>
        <g fill="#0F172A">{string.Concat(Enumerable.Range(0, 34).Select(i => $"""<rect x="{280 + i * 10}" y="640" width="{(i % 3 == 1 ? 7 : 4)}" height="70"/>"""))}</g>
        """;

    /// <summary>A customer-style installation snapshot placeholder for review photos.</summary>
    public static string ReviewPhoto(string productLabel, int variant)
    {
        var tone = variant % 3 == 0 ? "#334155" : variant % 3 == 1 ? "#3F3F46" : "#1E293B";
        return Wrap(1200, 900, $"""
            <rect width="1200" height="900" fill="{tone}"/>
            <rect x="120" y="140" width="960" height="620" rx="12" fill="#CBD5E1" stroke="#94A3B8" stroke-width="6"/>
            <rect x="180" y="200" width="840" height="30" fill="#94A3B8"/>
            <rect x="180" y="300" width="360" height="300" rx="14" fill="#E2E8F0" stroke="#64748B" stroke-width="4"/>
            <text x="360" y="465" font-family="Inter,Arial,sans-serif" font-size="30" font-weight="700" fill="#0F172A" text-anchor="middle">{Esc(Shorten(productLabel, 20))}</text>
            {Terminals(600, 330, 8)}{Terminals(600, 460, 8)}
            <rect x="180" y="660" width="840" height="30" fill="#94A3B8"/>
            <text x="60" y="860" font-family="Inter,Arial,sans-serif" font-size="26" fill="#CBD5E1">Customer photo · installed in control panel</text>
            """);
    }

    public static string BlurDataUrl(string hexColor)
    {
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 8 8"><rect width="8" height="8" fill="{hexColor}" opacity="0.18"/></svg>""";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
