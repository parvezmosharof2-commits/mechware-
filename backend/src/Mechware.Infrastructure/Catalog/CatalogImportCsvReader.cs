using System.Text;

namespace Mechware.Infrastructure.Catalog;

/// <summary>A file-level problem that stops the read (not a bad row).</summary>
public sealed class CatalogImportFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Streaming RFC 4180 reader: quoted fields, doubled quotes, line breaks inside quotes, CRLF or LF. A UTF-8 byte-order mark is
/// skipped. Reads one record at a time, so memory stays flat. Bytes are counted against <paramref name="maxBytes"/>.
/// </summary>
public sealed class CatalogImportCsvReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly CountingStream _counted;
    private readonly int _maxField;

    public CatalogImportCsvReader(Stream stream, long maxBytes, int maxFieldChars)
    {
        _counted = new CountingStream(stream, maxBytes);
        _reader = new StreamReader(_counted, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        _maxField = maxFieldChars;
    }

    /// <summary>The next record, or null at the end. Blank lines are skipped.</summary>
    public string[]? Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            while (true)
            {
                var fields = new List<string>();
                var sb = new StringBuilder();
                bool quoted = false, any = false, wasQuoted = false;
                int c;
                while ((c = _reader.Read()) >= 0)
                {
                    any = true;
                    char ch = (char)c;
                    if (quoted)
                    {
                        if (ch == '"')
                        {
                            if (_reader.Peek() == '"') { _reader.Read(); sb.Append('"'); }
                            else quoted = false;
                        }
                        else sb.Append(ch);
                    }
                    else if (ch == '"' && sb.Length == 0 && !wasQuoted) { quoted = wasQuoted = true; }
                    else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); wasQuoted = false; }
                    else if (ch == '\r') { if (_reader.Peek() == '\n') _reader.Read(); break; }
                    else if (ch == '\n') break;
                    else
                    {
                        if (wasQuoted) throw new CatalogImportFileException("MALFORMED_CSV", "Text after a closing quote.");
                        sb.Append(ch);
                    }

                    if (sb.Length > _maxField)
                        throw new CatalogImportFileException("FIELD_TOO_LONG", $"A field is longer than {_maxField} characters.");
                }

                if (quoted) throw new CatalogImportFileException("MALFORMED_CSV", "A quoted field is never closed.");
                if (!any) return null;
                fields.Add(sb.ToString());
                if (fields.Count == 1 && fields[0].Length == 0 && !wasQuoted) continue;   // blank line
                return [.. fields];
            }
        }
        catch (DecoderFallbackException)
        {
            throw new CatalogImportFileException("INVALID_ENCODING", "The file is not valid UTF-8.");
        }
    }

    public void Dispose() => _reader.Dispose();

    private sealed class CountingStream(Stream inner, long max) : Stream
    {
        private long _total;
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

        private int Count(int read)
        {
            _total += read;
            if (_total > max) throw new CatalogImportFileException("FILE_TOO_LARGE", $"The file is larger than {max / (1024 * 1024)} MB.");
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
