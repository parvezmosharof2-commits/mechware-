using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Properties.PropertyRunner;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.2 property test (seeded): random command sequences on one quote, some legal and many not. After every step: at most one
/// open (Draft | PendingApproval) and at most one live (Sent | Accepted) version, versions numbered 1 … n, and a version that
/// has left Draft for Sent or beyond never changes its buyer-visible content again (a refused command changes nothing at all).
/// </summary>
public class QuotePropertyTests
{
    private const int Rounds = 2_000;

    [Fact]
    public void RandomCommandSequences_KeepOneOpenOneLive_AndNeverChangeAnIssuedVersion()
    {
        var refused = 0;
        var accepted = 0;
        ForAll(4202, Rounds, (rng, _) =>
        {
            var quote = Drafted();
            var at = Now;
            var lapsed = false; // whether the order of the Accepted version (if any) lapsed before confirmation
            var frozen = new Dictionary<int, string>();

            for (var step = rng.Next(3, 16); step > 0; step--)
            {
                at = at.AddHours(rng.Next(1, 72));
                var n = rng.Next(1, quote.LatestVersionNo + 1);
                var v = quote.Version(n);
                var before = Snapshot(quote);
                var hash = rng.Next(5) == 0 ? Hash('e') : v.ContentHash ?? Hash();
                Action act = rng.Next(11) switch
                {
                    0 => () => quote.EditDraft(n, Content(rng.Next(1, 40), Line(V1, price: rng.NextInt64(1, 1_000_000)), Line(V3)), Editor, Options, at),
                    1 => () => quote.Send(n, Issue(quote, n, hash: Hash((char)('a' + n % 6))), Sender, lapsed, Options, at),
                    2 => () => quote.Send(n, Issue(quote, n, OverAuthority(quote, n), Hash((char)('a' + n % 6))), Sender, lapsed, Options, at),
                    3 => () => quote.Approve(n, Approver, hash, null, lapsed, at),
                    4 => () => quote.RejectApproval(n, Approver, "Not this price", at),
                    5 => () => quote.Recall(n, Author, at),
                    6 => () => quote.Accept(n, hash, Guid.NewGuid(), Buyer, lapsed, at),
                    7 => () => quote.Reject(n, Buyer, null, at),
                    8 => () => quote.Withdraw(n, Sender, "Wrong prices", at),
                    9 => () => quote.Revise(Guid.NewGuid(), Editor, lapsed, at),
                    _ => () => lapsed = rng.Next(2) == 0, // the order side: an order lapses (or a new one is live)
                };

                try
                {
                    act();
                    if (quote.Versions.Any(x => x.Status == QuoteVersionStatus.Accepted && x.AcceptedAtUtc == at)) { lapsed = false; accepted++; }
                }
                catch (DomainException)
                {
                    refused++;
                    Snapshot(quote).Should().Be(before, "a refused command changes nothing");
                }

                quote.Versions.Count(x => x.Status is QuoteVersionStatus.Draft or QuoteVersionStatus.PendingApproval).Should().BeLessThanOrEqualTo(1);
                quote.Versions.Count(x => x.Status is QuoteVersionStatus.Sent or QuoteVersionStatus.Accepted).Should().BeLessThanOrEqualTo(1);
                quote.Versions.Select(x => x.VersionNo).Should().Equal(Enumerable.Range(1, quote.LatestVersionNo));
                foreach (var x in quote.Versions.Where(x => x.Status is not (QuoteVersionStatus.Draft or QuoteVersionStatus.PendingApproval)))
                {
                    var content = Visible(x);
                    if (frozen.TryGetValue(x.VersionNo, out var was)) content.Should().Be(was, $"version {x.VersionNo} is {x.Status}");
                    else frozen[x.VersionNo] = content;
                    x.ValidUntilUtc.Should().Be(QuoteValidity.ValidUntilUtc(x.SentAtUtc!.Value, x.ValidityDays));
                }
            }
        });
        refused.Should().BeGreaterThan(Rounds, "illegal commands are exercised");
        accepted.Should().BeGreaterThan(Rounds / 20, "acceptances are exercised");
    }

    /// <summary>The buyer-visible content of an issued version (no statuses).</summary>
    private static string Visible(QuoteVersion v) =>
        string.Join("|", v.ValidityDays, v.PaymentTerms, v.ShippingMode, v.TermsText, v.BuyerNote, v.ContentHash, v.CompanySnapshot, v.PreparerName,
            v.SentAtUtc, string.Join(";", v.Lines.Select(l => $"{l.Id}:{l.VariantId}:{l.Quantity}:{l.UnitPriceMinor}:{l.Sku}:{l.PublicUnitPriceMinor}")));

    private static string Snapshot(Quote q) =>
        string.Join("#", q.Versions.Select(QuoteLifecycleTests.Fingerprint).Append($"{q.LatestVersionNo}:{q.ClosedAtUtc}:{q.DomainEvents.Count}"));
}
