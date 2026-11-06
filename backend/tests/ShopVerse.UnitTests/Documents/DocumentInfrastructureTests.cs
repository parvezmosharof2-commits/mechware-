using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using ShopVerse.Application.Catalog;
using ShopVerse.Infrastructure.Documents;

namespace ShopVerse.UnitTests.Documents;

/// <summary>Sprint 3–4 upload abuse gate: the scanner protocol, the development scanner and the object store.</summary>
public class DocumentInfrastructureTests : IDisposable
{
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mw-doc-unit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ----- clamd answers -----

    [Theory]
    [InlineData("stream: OK\0", ScanOutcome.Clean, null)]
    [InlineData("stream: OK\n", ScanOutcome.Clean, null)]
    [InlineData("stream: Eicar-Test-Signature FOUND\0", ScanOutcome.Infected, "Eicar-Test-Signature")]
    [InlineData("stream: Win.Trojan.Agent-123 FOUND\n", ScanOutcome.Infected, "Win.Trojan.Agent-123")]
    [InlineData("INSTREAM size limit exceeded. ERROR\0", ScanOutcome.Unavailable, null)]
    [InlineData("stream: lstat() failed ERROR\0", ScanOutcome.Unavailable, null)]
    [InlineData("", ScanOutcome.Unavailable, null)]
    [InlineData("stream: ok\0", ScanOutcome.Unavailable, null)]               // only the exact answer counts as clean
    [InlineData("stream: OK but not really\0", ScanOutcome.Unavailable, null)]
    [InlineData("OK\0", ScanOutcome.Unavailable, null)]
    [InlineData("garbage", ScanOutcome.Unavailable, null)]
    public void OnlyAnExactOk_IsClean_AndAnythingUnclearIsUnavailable(string answer, ScanOutcome outcome, string? signature) =>
        ClamAvScanner.Parse(answer).Should().Be(new ScanResult(outcome, signature));

    [Fact]
    public void ASignatureName_CannotCarryLineBreaksOrControlCharacters_IntoALog()
    {
        var result = ClamAvScanner.Parse("stream: Evil\r\n[ERROR] forged log line\u001b[31m FOUND\0");

        result.Outcome.Should().Be(ScanOutcome.Infected, "the file is still refused");
        result.Signature.Should().Be("Evil[ERROR] forged log line[31m").And.NotContainAny("\r", "\n", "\u001b", "\0");
        ClamAvScanner.Parse("stream: " + new string('a', 500) + " FOUND").Signature!.Length.Should().Be(100);
        ClamAvScanner.Parse("stream: Ünï-cödé FOUND").Signature.Should().Be("n-cd", "only printable ASCII is kept");
    }

    /// <summary>A stand-in for clamd: records what it was sent and answers.</summary>
    private sealed class FakeClamd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string? Command { get; private set; }
        public List<int> ChunkSizes { get; } = [];
        public MemoryStream Received { get; } = new();
        public Task Served { get; private set; } = Task.CompletedTask;

        public FakeClamd(Func<byte[], string> answer, bool hangAfterRequest = false)
        {
            _listener.Start();
            Served = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                byte[] command = new byte[10];
                await stream.ReadExactlyAsync(command);
                Command = Encoding.ASCII.GetString(command);
                byte[] size = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(size);
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(size);
                    if (length == 0) break;
                    ChunkSizes.Add(length);
                    byte[] chunk = new byte[length];
                    await stream.ReadExactlyAsync(chunk);
                    Received.Write(chunk);
                }

                if (hangAfterRequest) { await Task.Delay(TimeSpan.FromSeconds(30)); return; }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer(Received.ToArray())));
            });
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task TheScanner_SendsTheFileInLengthPrefixedChunks_AndReadsOk()
    {
        byte[] file = new byte[150_000];
        Random.Shared.NextBytes(file);
        using var clamd = new FakeClamd(_ => "stream: OK\0");

        var result = await new ClamAvScanner("127.0.0.1", clamd.Port, TimeSpan.FromSeconds(10)).ScanAsync(new MemoryStream(file));
        await clamd.Served;

        result.Outcome.Should().Be(ScanOutcome.Clean);
        clamd.Command.Should().Be("zINSTREAM\0");
        clamd.Received.ToArray().Should().Equal(file, "every byte arrives, in order");
        clamd.ChunkSizes.Should().OnlyContain(c => c > 0 && c <= 64 * 1024);
        clamd.ChunkSizes.Sum().Should().Be(file.Length);
    }

    [Fact]
    public async Task TheScanner_ReportsWhatTheDaemonFound()
    {
        using var clamd = new FakeClamd(sent => Encoding.ASCII.GetString(sent).Contains("EICAR") ? "stream: Eicar-Test-Signature FOUND\0" : "stream: OK\0");

        var result = await new ClamAvScanner("127.0.0.1", clamd.Port, TimeSpan.FromSeconds(10)).ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes(Eicar)));

        result.Should().Be(new ScanResult(ScanOutcome.Infected, "Eicar-Test-Signature"));
    }

    [Fact]
    public async Task ADaemonThatIsNotThere_IsUnavailable_NeverClean()
    {
        int freePort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); freePort = ((IPEndPoint)probe.LocalEndpoint).Port; }

        var result = await new ClamAvScanner("127.0.0.1", freePort, TimeSpan.FromSeconds(5)).ScanAsync(new MemoryStream([1, 2, 3]));

        result.Outcome.Should().Be(ScanOutcome.Unavailable);
    }

    [Fact]
    public async Task ADaemonThatDoesNotAnswerInTime_IsUnavailable_AfterTheTimeout_NotWhenItHangsUp()
    {
        // The stand-in hangs for 30 seconds and then closes the connection, which would also end as "unavailable", only slowly.
        // So the time is checked: the answer must come when the timeout runs out, not when the daemon gives up.
        using var clamd = new FakeClamd(_ => string.Empty, hangAfterRequest: true);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await new ClamAvScanner("127.0.0.1", clamd.Port, TimeSpan.FromMilliseconds(500)).ScanAsync(new MemoryStream([1, 2, 3]));

        result.Outcome.Should().Be(ScanOutcome.Unavailable);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACallerThatGivesUp_IsNotAScannerOutage_ItPropagates()
    {
        // A client closing the browser mid-upload cancels the request token. That must not look like "the scanner is down".
        using var clamd = new FakeClamd(_ => string.Empty, hangAfterRequest: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var scanner = new ClamAvScanner("127.0.0.1", clamd.Port, TimeSpan.FromSeconds(20));

        var act = () => scanner.ScanAsync(new MemoryStream([1, 2, 3]), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        using var already = new CancellationTokenSource();
        already.Cancel();
        var again = () => scanner.ScanAsync(new MemoryStream([1]), already.Token);
        await again.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ADaemonThatHangsUp_IsUnavailable()
    {
        using var clamd = new FakeClamd(_ => string.Empty);

        var result = await new ClamAvScanner("127.0.0.1", clamd.Port, TimeSpan.FromSeconds(5)).ScanAsync(new MemoryStream([1, 2, 3]));

        result.Outcome.Should().Be(ScanOutcome.Unavailable);
    }

    // ----- development scanner -----

    [Fact]
    public async Task TheDevelopmentScanner_FindsEicar_AnywhereInTheFile_AcrossReads()
    {
        var scanner = new DevelopmentSignatureScanner();
        byte[] Wrap(int before) => [.. new byte[before], .. Encoding.ASCII.GetBytes(Eicar), .. new byte[100]];

        foreach (int before in new[] { 0, 1, 65_000, 65_536, 65_550, 65_536 * 3 + 7 })
            (await scanner.ScanAsync(new MemoryStream(Wrap(before)))).Outcome.Should().Be(ScanOutcome.Infected, $"EICAR after {before} bytes");

        (await scanner.ScanAsync(new MemoryStream([.. Encoding.ASCII.GetBytes("%PDF-1.7\n"), .. Encoding.ASCII.GetBytes(Eicar)]))).Outcome
            .Should().Be(ScanOutcome.Infected, "behind a valid PDF header");
    }

    [Fact]
    public async Task TheDevelopmentScanner_PassesAnOrdinaryFile_AndAHalfSignature()
    {
        var scanner = new DevelopmentSignatureScanner();
        (await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 hello")))).Outcome.Should().Be(ScanOutcome.Clean);
        (await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes(Eicar[..30])))).Outcome.Should().Be(ScanOutcome.Clean);
        (await scanner.ScanAsync(new MemoryStream())).Outcome.Should().Be(ScanOutcome.Clean);
    }

    // ----- object store -----

    [Fact]
    public async Task TheStore_KeepsReadsAndDeletes_AndNeverOverwrites()
    {
        var store = new LocalObjectStorage(_root);
        await store.PutAsync("products/abc/one.pdf", new MemoryStream([1, 2, 3]));

        await using (var read = (await store.OpenReadAsync("products/abc/one.pdf"))!)
        {
            var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            copy.ToArray().Should().Equal(1, 2, 3);
        }

        var again = () => store.PutAsync("products/abc/one.pdf", new MemoryStream([9]));
        await again.Should().ThrowAsync<IOException>();

        await store.DeleteAsync("products/abc/one.pdf");
        (await store.OpenReadAsync("products/abc/one.pdf")).Should().BeNull();
        await store.DeleteAsync("products/abc/one.pdf");   // deleting what is gone is fine
        Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories).Should().BeEmpty("no temporary files are left behind");
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("products/../../outside.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("products\\evil.pdf")]
    [InlineData("Products/Upper.pdf")]
    [InlineData("products/a b.pdf")]
    [InlineData("products/a%2e%2e/b.pdf")]
    [InlineData("")]
    public async Task TheStore_RefusesAnyKeyThatIsNotOurs(string key)
    {
        var store = new LocalObjectStorage(_root);

        var put = () => store.PutAsync(key, new MemoryStream([1]));
        await put.Should().ThrowAsync<ArgumentException>();
        var read = () => store.OpenReadAsync(key);
        await read.Should().ThrowAsync<ArgumentException>();
        var delete = () => store.DeleteAsync(key);
        await delete.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheStore_RefusesAKeyThatLooksInside_ButIsNotOneOfOurs()
    {
        // Both stay inside the store, so the check on where the path ends up would let them through. They are still not keys
        // the server makes: no '..' anywhere, and never an absolute path (not even one that names a place inside the store).
        var store = new LocalObjectStorage(_root);
        string[] keys = ["products/../products/x.pdf", "products/a/../b/x.pdf", Path.Combine(_root, "products", "x.pdf").Replace('\\', '/')];

        foreach (var key in keys)
        {
            var put = () => store.PutAsync(key, new MemoryStream([1]));
            await put.Should().ThrowAsync<ArgumentException>(key);
        }

        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedWrite_LeavesNothingBehind()
    {
        var store = new LocalObjectStorage(_root);
        var broken = new ThrowingStream();

        var act = () => store.PutAsync("products/x/y.pdf", broken);

        await act.Should().ThrowAsync<IOException>();
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk on fire");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("disk on fire");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
