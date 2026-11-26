using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Invoicing;

namespace Mechware.UnitTests.Invoicing;

/// <summary>E6.5a, ADR-0012 §9.1–9.2, threat model §১৪ IT3 + IT17: the pure credit-note planner (caps per line and invoice, remainder rule, VAT).</summary>
public class CreditNotePlannerTests
{
    private static readonly decimal[] Rates = [0m, 0.05m, 0.075m, 0.10m, 0.15m];
    private static readonly DateTime Now = InvoiceFixtures.Now.AddDays(3);

    private static CreditNotePlan Plan(Invoice invoice, Dictionary<Guid, CreditedSoFar> credited, params CreditLineRequest[] lines) =>
        CreditNotePlanner.Plan(invoice, credited, lines, CreditNoteReason.GoodsReturned, "Goods returned by the buyer", Now, 180);

    private static void Add(Dictionary<Guid, CreditedSoFar> credited, CreditNotePlan plan)
    {
        foreach (var l in plan.Lines)
        {
            var c = credited.GetValueOrDefault(l.OriginalLineId) ?? CreditedSoFar.None;
            credited[l.OriginalLineId] = new CreditedSoFar(c.Quantity + l.Quantity, c.NetMinor + l.NetMinor, c.VatMinor + l.VatMinor);
        }
    }

    private static Invoice RandomInvoice(Random random)
    {
        var lines = Enumerable.Range(0, random.Next(1, 5)).Select(n =>
        {
            var qty = random.Next(1, 40);
            var unit = random.NextInt64(100, 900_000);   // at least one taka a unit
            return InvoiceFixtures.Line($"SKU-{n:D2}", qty, unit, Rates[random.Next(Rates.Length)], random.Next(3) == 0 ? random.NextInt64(0, qty * unit / 3 + 1) : 0);
        }).ToList();
        var company = random.Next(2) == 0 ? InvoiceFixtures.ActiveCompany(Guid.NewGuid()) : null;
        var order = InvoiceFixtures.Shipped(lines, random.Next(3) == 0 ? 0 : random.NextInt64(1, 50_000), company?.Id, out var shipment);
        return InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, company),
            InvoiceFixtures.Live with { ShippingVatRate = Rates[random.Next(Rates.Length)] });
    }

    /// <summary>IT3 (property): over random invoices and random (often too large) credit notes, the credited quantity, value and VAT never pass
    /// the original line, the totals never pass the invoice, a refusal is only ever given when something would pass, and a fully credited
    /// line ends at exactly its value and VAT.</summary>
    [Fact]
    public void ACreditNoteCannotExceedTheOriginal()
    {
        var random = new Random(20261126);
        var (issued, refused) = (0, 0);
        for (var run = 0; run < 300; run++)
        {
            var invoice = RandomInvoice(random);
            var credited = new Dictionary<Guid, CreditedSoFar>();
            for (var note = 0; note < 12; note++)
            {
                var asked = invoice.Lines.Where(_ => random.Next(2) == 0).Select(l =>
                {
                    var c = credited.GetValueOrDefault(l.Id) ?? CreditedSoFar.None;
                    var qty = random.Next(4) == 0 ? 0 : random.Next(1, l.Quantity - c.Quantity + 3);
                    long? net = qty == 0 || random.Next(3) == 0 ? random.NextInt64(1, l.NetMinor - c.NetMinor + 3) : null;
                    return new CreditLineRequest(l.Id, qty, net);
                }).ToArray();
                if (asked.Length == 0) continue;
                var wouldPass = asked.Any(a =>
                {
                    var l = invoice.Lines.Single(x => x.Id == a.OriginalLineId);
                    var c = credited.GetValueOrDefault(l.Id) ?? CreditedSoFar.None;
                    var net = a.NetMinor ?? CreditNotePlanner.NetForQuantity(l, c, a.Quantity);
                    return a.Quantity > l.Quantity - c.Quantity || net > l.NetMinor - c.NetMinor || net < 1;
                });
                try
                {
                    var plan = Plan(invoice, credited, asked);
                    wouldPass.Should().BeFalse("a plan is only made when every line fits");
                    plan.Lines.Should().OnlyContain(l => l.NetMinor >= 1 && l.VatMinor >= 0 && l.TotalMinor == l.NetMinor + l.VatMinor);
                    Add(credited, plan);
                    issued++;
                }
                catch (ConflictException ex) when (ex.Code == CreditNotePlanner.ExceedsOriginal)
                {
                    wouldPass.Should().BeTrue("a refusal needs a line that does not fit");
                    refused++;
                }

                foreach (var l in invoice.Lines)
                {
                    var c = credited.GetValueOrDefault(l.Id) ?? CreditedSoFar.None;
                    c.Quantity.Should().BeLessThanOrEqualTo(l.Quantity);
                    c.NetMinor.Should().BeLessThanOrEqualTo(l.NetMinor);
                    c.VatMinor.Should().BeLessThanOrEqualTo(l.VatMinor);
                    if (c.NetMinor == l.NetMinor) c.VatMinor.Should().Be(l.VatMinor, "a fully credited line sums to zero");
                }
                credited.Values.Sum(c => c.NetMinor + c.VatMinor).Should().BeLessThanOrEqualTo(invoice.TotalMinor);
            }
        }

        issued.Should().BeGreaterThan(500);
        refused.Should().BeGreaterThan(500);
    }

    /// <summary>IT3 + §10 remainder rule: crediting unit by unit gives net × 1 / quantity (banker's) each time and the last unit the rest; the line ends
    /// at exactly zero value and VAT left, and only the last step is "fully credited".</summary>
    [Fact]
    public void AFullyCreditedLine_SumsToZero_ToThePoisha()
    {
        var random = new Random(7);
        for (var run = 0; run < 200; run++)
        {
            var invoice = RandomInvoice(random);
            var credited = new Dictionary<Guid, CreditedSoFar>();
            foreach (var line in invoice.Lines)
                for (var unit = 1; unit <= line.Quantity; unit++)
                {
                    var left = line.NetMinor - (credited.GetValueOrDefault(line.Id)?.NetMinor ?? 0);
                    var expected = unit == line.Quantity ? left : (long)decimal.Round((decimal)line.NetMinor / line.Quantity, 0, MidpointRounding.ToEven);
                    var plan = Plan(invoice, credited, new CreditLineRequest(line.Id, 1));
                    plan.Lines.Single().NetMinor.Should().Be(expected);
                    plan.Lines.Single().FullyCredited.Should().Be(unit == line.Quantity);
                    Add(credited, plan);
                }

            foreach (var line in invoice.Lines)
                credited[line.Id].Should().Be(new CreditedSoFar(line.Quantity, line.NetMinor, line.VatMinor));
            credited.Values.Sum(c => c.NetMinor + c.VatMinor).Should().Be(invoice.TotalMinor);
        }

        var (company, _, _, _) = InvoiceFixtures.CompanyInvoice();
        var cable = company.Lines.Single(l => l.Sku == "CBL-10");                       // 25 × 123.45, VAT 7.5 %
        var soFar = new CreditedSoFar(24, cable.NetMinor - 12_345 - 1, 0);
        CreditNotePlanner.NetForQuantity(cable, soFar, 1).Should().Be(12_346, "the unit that completes the quantity takes the rest");
        CreditNotePlanner.NetForQuantity(cable, new CreditedSoFar(23, 0, 0), 1).Should().Be(12_345, "one unit before the last is still net / quantity");
        CreditNotePlanner.NetForQuantity(cable, CreditedSoFar.None, 25).Should().Be(cable.NetMinor);
    }

    /// <summary>The cap is per line, not only per invoice: one line over its own value is refused although the invoice total still has room.</summary>
    [Fact]
    public void OneLineOverItsOwnValue_IsRefused_EvenWhenTheInvoiceTotalHasRoom()
    {
        var (invoice, _, _, _) = InvoiceFixtures.CompanyInvoice();
        var cable = invoice.Lines.Single(l => l.Sku == "CBL-10");
        var act = () => Plan(invoice, [], new CreditLineRequest(cable.Id, 0, cable.NetMinor + 1));
        act.Should().Throw<ConflictException>().Which.Code.Should().Be(CreditNotePlanner.ExceedsOriginal);
        var more = () => Plan(invoice, [], new CreditLineRequest(cable.Id, cable.Quantity + 1));
        more.Should().Throw<ConflictException>().Which.Code.Should().Be(CreditNotePlanner.ExceedsOriginal);

        var plc = invoice.Lines.Single(l => l.Sku == "PLC-1200");
        var credited = new Dictionary<Guid, CreditedSoFar> { [plc.Id] = new(0, plc.NetMinor - 10, plc.VatMinor - 1) };
        var plan = Plan(invoice, credited, new CreditLineRequest(plc.Id, 0, 9));
        plan.Lines.Single().VatMinor.Should().BeLessThanOrEqualTo(1, "the VAT of a partial credit never passes the VAT left");
        Plan(invoice, credited, new CreditLineRequest(plc.Id, 2)).Lines.Single().Should().Match<PlannedCreditLine>(l =>
            l.NetMinor == 10 && l.VatMinor == 1 && l.FullyCredited);
    }

    /// <summary>IT3: only a tax invoice can be credited.</summary>
    [Fact]
    public void ACreditNoteOfACreditNote_IsRefused()
    {
        var (invoice, _, _, _) = InvoiceFixtures.CompanyInvoice();
        typeof(Invoice).GetProperty(nameof(Invoice.Kind))!.SetValue(invoice, InvoiceKind.CreditNote);   // a 6.7 loaded from the table
        var act = () => Plan(invoice, [], new CreditLineRequest(invoice.Lines[0].Id, 1));
        act.Should().Throw<ConflictException>().Which.Code.Should().Be(CreditNotePlanner.NotATaxInvoice);
    }

    /// <summary>IT3: up to and including the 180th Dhaka calendar day after the issue day; from the next Dhaka midnight 409.</summary>
    [Fact]
    public void ACreditNoteAfterTheMaxAge_IsRefused()
    {
        var (invoice, _, _, _) = InvoiceFixtures.CompanyInvoice();                         // issued 21-11-2026 15:00 Dhaka
        var line = new CreditLineRequest(invoice.Lines[0].Id, 1);
        var lastDay = new DateTime(2027, 5, 20, 17, 59, 59, DateTimeKind.Utc);              // 20-05-2027 23:59:59 Dhaka = issue day + 180
        CreditNotePlanner.Plan(invoice, new Dictionary<Guid, CreditedSoFar>(), [line], CreditNoteReason.PriceReduction, "Late but allowed", lastDay, 180)
            .Lines.Should().ContainSingle();
        var act = () => CreditNotePlanner.Plan(invoice, new Dictionary<Guid, CreditedSoFar>(), [line], CreditNoteReason.PriceReduction, "One day late",
            lastDay.AddSeconds(1), 180);
        act.Should().Throw<ConflictException>().Which.Code.Should().Be(CreditNotePlanner.TooLate);
        CreditNotePlanner.IsTooLate(invoice.IssuedAtUtc, lastDay.AddSeconds(1), 181).Should().BeFalse();
    }

    /// <summary>IT17: the reason is plain text on one line; control and invisible characters are removed before the length check.</summary>
    [Theory]
    [InlineData("  Wrong\u0000 BIN\r\n\ton the‮ invoice  ", "Wrong BIN on the invoice")]
    [InlineData("<b>Damaged</b>​ in transit", "<b>Damaged</b> in transit")]
    [InlineData("ভুল দাম ‌ছিল", "ভুল দাম ‌ছিল")]
    public void CreditNoteReason_IsCleanedToPlainText(string raw, string stored) => CreditNotePlanner.CleanReason(raw).Should().Be(stored);

    [Theory]
    [InlineData(null)]
    [InlineData("ab\u0001\u0002​  c")]
    public void ATooShortReason_IsRefused(string? raw)
    {
        var act = () => CreditNotePlanner.CleanReason(raw);
        act.Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("reasonText");
        var tooLong = () => CreditNotePlanner.CleanReason(new string('x', 501));
        tooLong.Should().Throw<ValidationException>();
    }

    [Fact]
    public void BadLines_AreRefusedBeforeAnythingIsPlanned()
    {
        var (invoice, _, _, _) = InvoiceFixtures.CompanyInvoice();
        var id = invoice.Lines[0].Id;
        foreach (var (lines, field) in new (CreditLineRequest[], string)[]
        {
            ([], "lines"), ([new(id, 1), new(id, 1)], "lines"), ([new(Guid.NewGuid(), 1)], "lines[0].originalLineId"),
            ([new(id, -1)], "lines[0].quantity"), ([new(id, 1, 0)], "lines[0].netMinor"), ([new(id, 0)], "lines[0]")
        })
        {
            var act = () => Plan(invoice, [], lines);
            act.Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be(field);
        }
    }
}
