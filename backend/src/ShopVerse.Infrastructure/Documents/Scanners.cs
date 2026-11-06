using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using ShopVerse.Application.Catalog;

namespace ShopVerse.Infrastructure.Documents;

/// <summary>
/// Scans through a ClamAV daemon (clamd) with the INSTREAM command: the file goes over TCP in length-prefixed chunks, and the
/// daemon answers <c>stream: OK</c>, <c>stream: NAME FOUND</c> or <c>... ERROR</c>. Anything but a clear answer is
/// <see cref="ScanOutcome.Unavailable"/>: an unreachable daemon, a timeout, an error such as a file over clamd's
/// <c>StreamMaxLength</c> (set it above the largest upload limit). A file is never called clean without the daemon saying so.
/// </summary>
public sealed class ClamAvScanner(string host, int port, TimeSpan timeout) : IVirusScanner
{
    private const int ChunkSize = 64 * 1024;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            await using var network = client.GetStream();

            await network.WriteAsync("zINSTREAM\0"u8.ToArray(), cts.Token);
            byte[] buffer = new byte[ChunkSize];
            byte[] prefix = new byte[4];
            int read;
            while ((read = await content.ReadAsync(buffer, cts.Token)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(prefix, read);
                await network.WriteAsync(prefix, cts.Token);
                await network.WriteAsync(buffer.AsMemory(0, read), cts.Token);
            }

            BinaryPrimitives.WriteInt32BigEndian(prefix, 0);
            await network.WriteAsync(prefix, cts.Token);
            await network.FlushAsync(cts.Token);

            using var response = new MemoryStream();
            byte[] one = new byte[256];
            while (response.Length < 4096)
            {
                int n = await network.ReadAsync(one, cts.Token);
                if (n == 0) break;
                response.Write(one, 0, n);
                if (one.AsSpan(0, n).Contains((byte)0) || one.AsSpan(0, n).Contains((byte)'\n')) break;
            }

            return Parse(Encoding.UTF8.GetString(response.ToArray()));
        }
        // The caller giving up (the request was aborted) is not a scanner outage: it propagates. Only our own timeout, a refused
        // connection or a dropped one say "unavailable".
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return new ScanResult(ScanOutcome.Unavailable);
        }
    }

    /// <summary>
    /// The name a daemon gives a signature ends up in logs and messages. It is cut to 100 printable ASCII characters, so a
    /// reply with line breaks or control characters cannot add fake lines to a log.
    /// </summary>
    internal static string SafeName(string name) =>
        new string([.. name.Where(c => c is >= ' ' and <= '~').Take(100)]);

    /// <summary>Reads clamd's answer; only an exact OK is clean.</summary>
    public static ScanResult Parse(string answer)
    {
        string text = answer.Trim('\0', '\n', '\r', ' ');
        if (text == "stream: OK") return new ScanResult(ScanOutcome.Clean);
        if (text.StartsWith("stream: ", StringComparison.Ordinal) && text.EndsWith(" FOUND", StringComparison.Ordinal))
            return new ScanResult(ScanOutcome.Infected, SafeName(text["stream: ".Length..^" FOUND".Length]));
        return new ScanResult(ScanOutcome.Unavailable);
    }
}

/// <summary>
/// DEVELOPMENT AND TESTS ONLY. Recognises nothing but the standard EICAR test string, so uploads can be tried without a
/// daemon. It is not a virus scanner: the API refuses to start outside Development without a real one (<see cref="ClamAvScanner"/>).
/// </summary>
public sealed class DevelopmentSignatureScanner : IVirusScanner
{
    private static readonly byte[] Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"u8.ToArray();

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        // Overlap by one signature length so a match across two reads is still found.
        byte[] buffer = new byte[64 * 1024 + Eicar.Length];
        int carry = 0;
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(carry), ct)) > 0)
        {
            int length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf(Eicar) >= 0) return new ScanResult(ScanOutcome.Infected, "Eicar-Test-Signature");
            carry = Math.Min(Eicar.Length - 1, length);
            Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
        }

        return new ScanResult(ScanOutcome.Clean);
    }
}
