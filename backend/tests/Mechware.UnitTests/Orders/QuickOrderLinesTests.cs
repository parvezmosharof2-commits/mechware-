using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Mechware.Domain.Orders;

namespace Mechware.UnitTests.Orders;

/// <summary>D5.1: the pure quick-order / BOM line parser.</summary>
public class QuickOrderLinesTests
{
    private static QuickOrderLine One(string text)
    {
        var r = QuickOrderLines.Parse(text);
        r.Errors.Should().BeEmpty();
        return r.Lines.Should().ContainSingle().Subject;
    }

    [Theory]
    [InlineData("LC1D09 5", "LC1D09", 5)]
    [InlineData("LC1D09\t5", "LC1D09", 5)]                       // Excel paste
    [InlineData("LC1D09, 5", "LC1D09", 5)]
    [InlineData("LC1D09;5", "LC1D09", 5)]
    [InlineData("5 x LC1D09", "LC1D09", 5)]
    [InlineData("5x LC1D09", "LC1D09", 5)]
    [InlineData("5 × LC1D09", "LC1D09", 5)]
    [InlineData("LC1D09 x 5", "LC1D09", 5)]
    [InlineData("\"LC1D09\",\"5\"", "LC1D09", 5)]
    [InlineData("LC1D09 ৫", "LC1D09", 5)]                        // Bangla digit
    [InlineData("LC1D09 ১,০০০", "LC1D09", 1000)]
    [InlineData("LC1D09 1,000", "LC1D09", 1000)]
    [InlineData("LC1D09,1,000", "LC1D09", 1000)]
    [InlineData("LC1D09\t\"1,000\"", "LC1D09", 1000)]
    [InlineData("LC1D09\t5\t\t", "LC1D09", 5)]
    [InlineData("  LC1D09   5  ", "LC1D09", 5)]
    public void Formats_are_read(string text, string partKey, int qty)
    {
        var l = One(text);
        l.PartKey.Should().Be(partKey);
        l.Quantity.Should().Be(qty);
    }

    [Theory]
    [InlineData("6ES7 214-1AG40-0XB0 5", "6ES7 214-1AG40-0XB0", 5)]
    [InlineData("6ES7 214-1AG40-0XB0, 5", "6ES7 214-1AG40-0XB0", 5)]
    [InlineData("6ES7 214-1AG40-0XB0\t5", "6ES7 214-1AG40-0XB0", 5)]
    [InlineData("5 x 6ES7 214-1AG40-0XB0", "6ES7 214-1AG40-0XB0", 5)]
    [InlineData("6ES7 214-1AG40-0XB0 x 5", "6ES7 214-1AG40-0XB0", 5)]
    public void Part_numbers_with_spaces_are_not_split_wrongly(string text, string partRaw, int qty)
    {
        var l = One(text);
        l.PartRaw.Should().Be(partRaw);
        l.PartKey.Should().Be("6ES72141AG400XB0");
        l.Quantity.Should().Be(qty);
    }

    [Fact]
    public void Part_only_line_is_one_piece_with_a_notice_and_a_spaced_part_stays_whole()
    {
        var r = QuickOrderLines.Parse("6ES7 214-1AG40-0XB0\n2900011");
        r.Errors.Should().BeEmpty();
        r.Lines.Select(l => (l.PartKey, l.Quantity)).Should().Equal(("6ES72141AG400XB0", 1), ("2900011", 1));
        r.Notices.Select(n => (n.Code, n.LineNumber)).Should().Equal(("QTY_DEFAULTED", 1), ("QTY_DEFAULTED", 2));
    }

    [Fact]
    public void Header_comments_and_blank_lines_are_skipped_and_numbers_stay_physical()
    {
        foreach (string header in new[] { "Part No\tQty", "Part Number, Quantity", "part no. qty", "পার্ট নম্বর\tপরিমাণ", "Qty;Part" })
        {
            var r = QuickOrderLines.Parse($"{header}\n\n# note\nLC1D09 2\n");
            r.Errors.Should().BeEmpty(header);
            r.Lines.Should().ContainSingle().Which.LineNumbers.Should().Equal(4);
        }
    }

    [Fact]
    public void Header_is_only_looked_for_on_the_first_content_line()
    {
        var r = QuickOrderLines.Parse("LC1D09 2\nQty\nLC1D12 1");
        r.Lines.Should().HaveCount(3);   // "Qty" later on is a (weird) part number, not silently dropped
    }

    [Fact]
    public void O_and_zero_confusion_gives_the_same_fuzzy_key_but_distinct_exact_keys()
    {
        var r = QuickOrderLines.Parse("LC1D09 1\nLC1DO9 1\nLCID09 1");
        r.Lines.Should().HaveCount(3);
        r.Lines.Select(l => l.FuzzyKey).Distinct().Should().ContainSingle().Which.Should().Be("1C1D09");
        r.Lines.Select(l => l.PartKey).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("LC1D09 2.5", "QTY_NOT_INTEGER")]
    [InlineData("LC1D09 2,5", "QTY_NOT_INTEGER")]
    [InlineData("LC1D09\t2.0", "QTY_NOT_INTEGER")]
    [InlineData("LC1D09 0", "QTY_INVALID")]
    [InlineData("LC1D09 -3", "QTY_INVALID")]
    [InlineData("LC1D09\tfive", "QTY_INVALID")]
    [InlineData("LC1D09\t", "QTY_MISSING")]
    [InlineData("LC1D09,", "QTY_MISSING")]
    [InlineData("LC1D09 1000001", "QTY_TOO_LARGE")]
    [InlineData("LC1D09 99999999999999", "QTY_TOO_LARGE")]
    [InlineData("\t5", "PART_MISSING")]
    [InlineData("---\t5", "PART_INVALID")]
    [InlineData("a\tb\tc", "TOO_MANY_FIELDS")]
    public void Bad_lines_are_reported_with_their_line_number(string bad, string code)
    {
        var r = QuickOrderLines.Parse($"LC1D12 1\n{bad}\nLC1D18 2");
        r.Lines.Select(l => l.PartKey).Should().Equal("LC1D12", "LC1D18");
        var e = r.Errors.Should().ContainSingle().Subject;
        e.Code.Should().Be(code);
        e.LineNumber.Should().Be(2);
        e.Message.Should().NotBeEmpty();
        r.IsClean.Should().BeFalse();
    }

    [Fact]
    public void Quantity_limit_is_inclusive()
        => One($"LC1D09 {QuickOrderLines.MaxQuantity}").Quantity.Should().Be(QuickOrderLines.MaxQuantity);

    [Fact]
    public void Duplicates_are_merged_with_a_notice_listing_the_lines()
    {
        var r = QuickOrderLines.Parse("LC1D09 2\nLC1D12 1\nlc1-d09\t3\n5 x LC1D09");
        r.Lines.Should().HaveCount(2);
        r.Lines[0].Quantity.Should().Be(10);
        r.Lines[0].LineNumbers.Should().Equal(1, 3, 4);
        r.Notices.Should().ContainSingle(n => n.Code == "DUPLICATE_MERGED").Which.RelatedLines.Should().Equal(1, 3, 4);
    }

    [Fact]
    public void A_merge_that_passes_the_limit_is_an_error_on_the_later_line()
    {
        var r = QuickOrderLines.Parse("LC1D09 600000\nLC1D09 600000");
        r.Lines.Single().Quantity.Should().Be(600000);
        var e = r.Errors.Should().ContainSingle().Subject;
        e.Code.Should().Be("QTY_TOO_LARGE");
        e.LineNumber.Should().Be(2);
    }

    [Fact]
    public void Five_hundred_lines_parse_fast_and_501_are_refused()
    {
        static string Build(int n) => string.Join('\n', Enumerable.Range(0, n).Select(i => $"PN{i:D5}-X {i % 50 + 1}"));
        QuickOrderLines.Parse(Build(10));   // warm up
        var sw = Stopwatch.StartNew();
        var r = QuickOrderLines.Parse(Build(500));
        sw.Stop();
        r.Lines.Should().HaveCount(500);
        r.Errors.Should().BeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);

        // Worst cases for the backtracking patterns (ThousandsTail, PartXQty, DecimalCommaTail): 500 lines of ~200 characters
        // that almost match and then fail at the end. The whole input must still parse quickly (no ReDoS).
        string[] hostile =
        [
            "A" + string.Concat(Enumerable.Repeat(" ,1", 64)) + "x",
            "A" + string.Concat(Enumerable.Repeat(" x 1", 48)) + " x",
            "A " + string.Concat(Enumerable.Repeat("1,", 95)) + "1,,",
            string.Concat(Enumerable.Repeat("1,000", 39)) + ",00",
        ];
        string evil = string.Join("\n", Enumerable.Range(0, 500).Select(i => hostile[i % hostile.Length]));
        sw.Restart();
        QuickOrderLines.Parse(evil);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "pathological lines must not backtrack catastrophically");

        var over = QuickOrderLines.Parse(Build(501));
        over.InputError!.Code.Should().Be("TOO_MANY_LINES");
        over.Lines.Should().BeEmpty();
        over.IsClean.Should().BeFalse();
    }

    [Fact]
    public void Blank_comment_and_header_lines_do_not_count_toward_the_limit()
    {
        string body = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"PN{i:D5} 1"));
        QuickOrderLines.Parse("Part No\tQty\n\n# x\n" + body).Lines.Should().HaveCount(500);
    }

    [Fact]
    public void Oversize_input_and_long_lines_are_refused()
    {
        QuickOrderLines.Parse(new string('A', QuickOrderLines.MaxInputLength + 1)).InputError!.Code.Should().Be("INPUT_TOO_LARGE");
        var r = QuickOrderLines.Parse($"{new string('A', 201)} 1\nLC1D09 1");
        r.Errors.Should().ContainSingle(e => e.Code == "LINE_TOO_LONG" && e.LineNumber == 1);
        r.Lines.Should().ContainSingle();
        QuickOrderLines.Parse($"{new string('A', 65)} 1").Errors.Should().ContainSingle(e => e.Code == "PART_TOO_LONG");
    }

    [Fact]
    public void Control_characters_are_stripped_and_text_is_nfc()
    {
        var l = One("﻿LC1\u0000D09​ 5\r\n");
        l.PartKey.Should().Be("LC1D09");
        One("CaféX 2").PartRaw.Should().Be("CaféX");   // NFC composed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\t\n# only a comment")]
    public void Empty_input_is_empty_and_clean(string? text)
    {
        var r = QuickOrderLines.Parse(text);
        r.Lines.Should().BeEmpty();
        r.IsClean.Should().BeTrue();
    }

    [Fact]
    public void Random_valid_lines_round_trip_in_every_format()
    {
        var rnd = new Random(20261113);
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ0123456789";
        string Word(int min, int max) => new(Enumerable.Range(0, rnd.Next(min, max)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
        for (int round = 0; round < 200; round++)
        {
            var expected = new List<(string Key, int Qty)>();
            var text = new List<string>();
            var seen = new HashSet<string>();
            for (int i = 0; i < 20; i++)
            {
                string part;
                do
                {
                    string a = Word(4, 8), b = Word(3, 6);
                    part = rnd.Next(2) == 0 ? a : $"{a} {b}";
                    if (part.Contains(' ') && part.Split(' ')[^1].All(char.IsAsciiDigit)) part = part.Replace(' ', '-');   // rule 3 would read a trailing number as qty
                    if (part.All(char.IsAsciiDigit) || part.Split(' ')[0].All(char.IsAsciiDigit)) part = "Z" + part;       // "5 x ..." shape
                } while (!seen.Add(part.Replace(" ", "").Replace("-", "")));

                int qty = rnd.Next(1, 1_000_000);
                string q = rnd.Next(3) == 0 ? qty.ToString("N0", CultureInfo.InvariantCulture) : qty.ToString(CultureInfo.InvariantCulture);
                text.Add(rnd.Next(5) switch
                {
                    0 => $"{part}\t{q}",
                    1 => $"{part}, \"{q}\"",
                    2 => $"{qty} x {part}",
                    3 => $"{part} x {qty}",
                    _ => $"{part};{q}",
                });
                expected.Add((part.Replace(" ", "").Replace("-", ""), qty));
            }

            var r = QuickOrderLines.Parse(string.Join('\n', text));
            r.Errors.Should().BeEmpty(string.Join(" | ", text));
            r.Lines.Select(l => (l.PartKey, l.Quantity)).Should().Equal(expected);
        }
    }
}
