using System.Buffers;
using System.Text;

namespace Wcc.Core.Wml;

/// <summary>
/// One text fragment and the last engine line command observed before it.
/// TextOffset counts UTF-16 characters within its logical line. The marker is
/// an unparsed engine anchor, not an exact authored span or edit authority.
/// MarkerLogicalLineIndex identifies where that anchor was observed; it does
/// not imply that later logical lines advance an authored source line.
/// </summary>
public sealed record WesnothPlainSourceSegment(
    int TextOffset,
    string Text,
    string? RawLineMarker,
    long? MarkerLogicalLineIndex);

/// <summary>
/// An engine command at a UTF-16 offset in a logical line. Multiple commands
/// at the same offset retain their original order, including commands with
/// no intervening text. These can carry otherwise invisible macro entries.
/// </summary>
public sealed record WesnothPlainSourceCommand(int TextOffset, string RawCommand);

public sealed record WesnothPlainLogicalLine(
    long LogicalLineIndex,
    string Text,
    IReadOnlyList<WesnothPlainSourceSegment> Segments,
    IReadOnlyList<WesnothPlainSourceCommand> Commands,
    string? SourceMarkerAtEnd,
    long? MarkerLogicalLineIndexAtEnd);

public sealed record WesnothPlainSourceReaderOptions(
    int ReadBufferBytes = 64 * 1024,
    int MaxPhysicalLineBytes = 8 * 1024 * 1024,
    int MaxLogicalLineCharacters = 16 * 1024 * 1024,
    int MaxSegmentsPerLogicalLine = 65_536,
    int MaxCommandsPerLogicalLine = 65_536,
    int MaxCommandCharactersPerLogicalLine = 16 * 1024 * 1024);

/// <summary>
/// Streams Wesnoth's byte-oriented .plain format without retaining a whole
/// campaign. Literal byte 0xfe starts a command consuming its physical newline;
/// that newline does not end the surrounding WML logical line. Paths/line stacks
/// remain intact because whitespace splitting cannot safely identify filenames.
/// Consumers must separately join evidence to verified source revisions.
/// </summary>
public static class WesnothPlainSourceReader
{
    private const byte CommandMarker = 0xfe;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task ReadAsync(
        string path,
        Action<WesnothPlainLogicalLine> onLine,
        CancellationToken cancellationToken = default,
        WesnothPlainSourceReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(onLine);
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        await ReadAsync(input, onLine, cancellationToken, options).ConfigureAwait(false);
    }

    /// <summary>Leaves the caller-owned input stream open.</summary>
    public static async Task ReadAsync(
        Stream input,
        Action<WesnothPlainLogicalLine> onLine,
        CancellationToken cancellationToken = default,
        WesnothPlainSourceReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(onLine);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new WesnothPlainSourceReaderOptions();
        ValidateOptions(options);

        var state = new ReaderState(onLine, options, cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(options.ReadBufferBytes);
        var physicalLine = new ArrayBufferWriter<byte>(Math.Min(options.ReadBufferBytes, 4096));
        try
        {
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(
                       buffer.AsMemory(0, options.ReadBufferBytes), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                var start = 0;
                while (start < bytesRead)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var newline = buffer.AsSpan(start, bytesRead - start).IndexOf((byte)'\n');
                    var length = newline < 0 ? bytesRead - start : newline;
                    if (length > options.MaxPhysicalLineBytes - physicalLine.WrittenCount)
                        throw LimitExceeded("physical-line byte", options.MaxPhysicalLineBytes);
                    if (physicalLine.WrittenCount == 0 && newline >= 0)
                    {
                        state.ProcessPhysicalLine(buffer.AsSpan(start, length), hasNewline: true);
                    }
                    else
                    {
                        buffer.AsSpan(start, length).CopyTo(physicalLine.GetSpan(length));
                        physicalLine.Advance(length);
                        if (newline >= 0)
                        {
                            state.ProcessPhysicalLine(physicalLine.WrittenSpan, hasNewline: true);
                            physicalLine.Clear();
                        }
                    }
                    start += length + (newline < 0 ? 0 : 1);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (physicalLine.WrittenCount > 0)
                state.ProcessPhysicalLine(physicalLine.WrittenSpan, hasNewline: false);
            state.Finish();
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Wesnoth .plain output contains invalid UTF-8 outside its command marker.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateOptions(WesnothPlainSourceReaderOptions options)
    {
        if (options.ReadBufferBytes <= 0 || options.MaxPhysicalLineBytes <= 0 ||
            options.MaxLogicalLineCharacters <= 0 || options.MaxSegmentsPerLogicalLine <= 0 ||
            options.MaxCommandsPerLogicalLine <= 0 || options.MaxCommandCharactersPerLogicalLine <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Reader bounds must all be positive.");
    }

    private static InvalidDataException LimitExceeded(string kind, int maximum) =>
        new($"Wesnoth .plain output exceeds the configured {kind} limit ({maximum}).");

    private sealed class ReaderState(
        Action<WesnothPlainLogicalLine> onLine,
        WesnothPlainSourceReaderOptions options,
        CancellationToken cancellationToken)
    {
        private readonly StringBuilder _text = new();
        private readonly List<WesnothPlainSourceSegment> _segments = [];
        private readonly List<WesnothPlainSourceCommand> _commands = [];
        private string? _sourceMarker;
        private long? _markerLogicalLineIndex;
        private long _logicalLineIndex;
        private int _commandCharacters;

        public void ProcessPhysicalLine(ReadOnlySpan<byte> bytes, bool hasNewline)
        {
            if (hasNewline && !bytes.IsEmpty && bytes[^1] == (byte)'\r')
                bytes = bytes[..^1];
            var markerIndex = bytes.IndexOf(CommandMarker);
            if (markerIndex < 0)
            {
                AppendText(bytes);
                if (hasNewline)
                    EmitLine();
                return;
            }

            AppendText(bytes[..markerIndex]);
            if (_commands.Count == options.MaxCommandsPerLogicalLine)
                throw LimitExceeded("commands-per-logical-line", options.MaxCommandsPerLogicalLine);
            var rawCommand = StrictUtf8.GetString(bytes[(markerIndex + 1)..]);
            if (rawCommand.Length > options.MaxCommandCharactersPerLogicalLine - _commandCharacters)
                throw LimitExceeded("command-characters-per-logical-line", options.MaxCommandCharactersPerLogicalLine);
            _commandCharacters += rawCommand.Length;
            _commands.Add(new WesnothPlainSourceCommand(_text.Length, rawCommand));
            var command = rawCommand.AsSpan().TrimStart();
            if (command.StartsWith("line ", StringComparison.Ordinal))
            {
                _sourceMarker = rawCommand;
                _markerLogicalLineIndex = _logicalLineIndex;
            }
            else if (!command.StartsWith("textdomain ", StringComparison.Ordinal))
            {
                // An unsupported command might change location semantics. Keep
                // its evidence, but never carry a prior location across it.
                _sourceMarker = null;
                _markerLogicalLineIndex = null;
            }
        }

        private void AppendText(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;
            var text = StrictUtf8.GetString(bytes);
            if (text.Length > options.MaxLogicalLineCharacters - _text.Length)
                throw LimitExceeded("logical-line character", options.MaxLogicalLineCharacters);
            if (_segments.Count == options.MaxSegmentsPerLogicalLine)
                throw LimitExceeded("segments-per-logical-line", options.MaxSegmentsPerLogicalLine);
            _segments.Add(new WesnothPlainSourceSegment(
                _text.Length, text, _sourceMarker, _markerLogicalLineIndex));
            _text.Append(text);
        }

        public void Finish()
        {
            if (_text.Length > 0 || _commands.Count > 0)
                EmitLine();
        }

        private void EmitLine()
        {
            cancellationToken.ThrowIfCancellationRequested();
            onLine(new WesnothPlainLogicalLine(
                _logicalLineIndex++, _text.ToString(),
                Array.AsReadOnly(_segments.ToArray()), Array.AsReadOnly(_commands.ToArray()),
                _sourceMarker, _markerLogicalLineIndex));
            _text.Clear();
            _segments.Clear();
            _commands.Clear();
            _commandCharacters = 0;
        }
    }
}
