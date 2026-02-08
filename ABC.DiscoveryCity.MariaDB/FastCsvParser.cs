using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// Ultra-lean Span-based CSV parser. Reads chunks into a buffer and uses
/// Span/pointer arithmetic to parse fields without allocating strings until
/// the final value extraction.
/// 
/// Key design:
/// - Single large buffer, read in chunks
/// - Span-based field scanning - no intermediate string allocations
/// - Only allocates strings when extracting final field values
/// - Schema-aware: knows column count, parses exactly N fields per row
/// </summary>
public sealed class SpanCsvParser : IDisposable
{
    private readonly Stream _stream;
    private readonly GZipStream? _gzip;
    private readonly int _columnCount;
    
    // Large buffer - reused across reads
    private byte[] _buffer;
    private int _bufferLen;
    private int _bufferPos;
    private const int BufferSize = 4 * 1024 * 1024; // 4MB buffer
    
    // Tracking
    private long _rowNumber;
    private long _bytesRead;
    private bool _eof;
    
    public long RowNumber => _rowNumber;
    public long BytesRead => _bytesRead;
    public int ColumnCount => _columnCount;

    public SpanCsvParser(string gzipFilePath, int columnCount)
    {
        _columnCount = columnCount;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        
        _stream = new FileStream(gzipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        _gzip = new GZipStream(_stream, CompressionMode.Decompress);
    }

    public SpanCsvParser(Stream stream, int columnCount, bool isGzipped = true)
    {
        _columnCount = columnCount;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        _stream = stream;
        
        if (isGzipped)
        {
            _gzip = new GZipStream(stream, CompressionMode.Decompress);
        }
    }

    /// <summary>
    /// Skip N rows efficiently without allocating strings.
    /// Returns actual number of rows skipped.
    /// </summary>
    public long SkipRows(long count)
    {
        long skipped = 0;
        
        while (skipped < count && !_eof)
        {
            // Skip one row by scanning for N fields
            if (SkipOneRow())
            {
                skipped++;
                _rowNumber++;
            }
            else
            {
                break;
            }
        }
        
        return skipped;
    }

    /// <summary>
    /// Skip a single row by scanning through N fields without extracting values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipOneRow()
    {
        for (int field = 0; field < _columnCount; field++)
        {
            bool isLast = (field == _columnCount - 1);
            if (!SkipField(isLast))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Skip a single field without extracting its value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipField(bool isLastField)
    {
        if (!EnsureData()) return false;
        
        byte b = _buffer[_bufferPos];
        
        // Quoted field
        if (b == (byte)'"')
        {
            _bufferPos++;
            return SkipQuotedContent(isLastField);
        }
        
        // NULL: \N
        if (b == (byte)'\\')
        {
            _bufferPos++;
            if (EnsureData() && _buffer[_bufferPos] == (byte)'N')
            {
                _bufferPos++;
            }
            return SkipToDelimiter(isLastField);
        }
        
        // Unquoted
        return SkipToDelimiter(isLastField);
    }

    /// <summary>
    /// Skip quoted field content until closing quote + delimiter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipQuotedContent(bool isLastField)
    {
        while (true)
        {
            if (!EnsureData()) return false;
            
            byte b = _buffer[_bufferPos++];
            
            if (b == (byte)'"')
            {
                if (!EnsureData()) return true; // EOF after quote is OK
                
                byte next = _buffer[_bufferPos];
                if (next == (byte)'"')
                {
                    // Escaped quote ""
                    _bufferPos++;
                }
                else
                {
                    // End of quoted field - skip delimiter
                    return SkipDelimiter(isLastField);
                }
            }
            else if (b == (byte)'\\')
            {
                // Skip escape sequence
                if (EnsureData()) _bufferPos++;
            }
            // Other bytes (including newlines) are just content
        }
    }

    /// <summary>
    /// Skip to next delimiter (comma or newline).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipToDelimiter(bool isLastField)
    {
        while (true)
        {
            if (!EnsureData()) return false;
            
            byte b = _buffer[_bufferPos];
            
            if (b == (byte)',' && !isLastField)
            {
                _bufferPos++;
                return true;
            }
            if (b == (byte)'\n')
            {
                _bufferPos++;
                return true;
            }
            if (b == (byte)'\r')
            {
                _bufferPos++;
                if (EnsureData() && _buffer[_bufferPos] == (byte)'\n')
                    _bufferPos++;
                return true;
            }
            _bufferPos++;
        }
    }

    /// <summary>
    /// Skip the delimiter after a quoted field.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SkipDelimiter(bool isLastField)
    {
        if (!EnsureData()) return true;
        
        byte b = _buffer[_bufferPos];
        
        if (b == (byte)',' && !isLastField)
        {
            _bufferPos++;
            return true;
        }
        if (b == (byte)'\n')
        {
            _bufferPos++;
            return true;
        }
        if (b == (byte)'\r')
        {
            _bufferPos++;
            if (EnsureData() && _buffer[_bufferPos] == (byte)'\n')
                _bufferPos++;
            return true;
        }
        
        return true;
    }

    /// <summary>
    /// Read the next row, returning field values.
    /// Only allocates strings for the actual field content.
    /// </summary>
    public bool ReadRow(Span<string?> fields)
    {
        if (fields.Length < _columnCount)
            throw new ArgumentException($"Fields span must have at least {_columnCount} elements");
        
        if (!EnsureData()) return false;
        
        for (int i = 0; i < _columnCount; i++)
        {
            bool isLast = (i == _columnCount - 1);
            fields[i] = ReadField(isLast);
            
            if (fields[i] == null && i == 0 && _eof)
                return false; // Clean EOF at row boundary
        }
        
        _rowNumber++;
        return true;
    }

    /// <summary>
    /// Read a single field value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? ReadField(bool isLastField)
    {
        if (!EnsureData()) return null;
        
        byte b = _buffer[_bufferPos];
        
        // Quoted field - most common
        if (b == (byte)'"')
        {
            _bufferPos++;
            return ReadQuotedField(isLastField);
        }
        
        // NULL: \N
        if (b == (byte)'\\')
        {
            _bufferPos++;
            if (EnsureData() && _buffer[_bufferPos] == (byte)'N')
            {
                _bufferPos++;
                SkipDelimiter(isLastField);
                return "\\N"; // Marker for NULL
            }
            // Not \N, treat as unquoted starting with backslash
            return ReadUnquotedField(isLastField, includeLeadingBackslash: true);
        }
        
        // Unquoted field
        return ReadUnquotedField(isLastField, includeLeadingBackslash: false);
    }

    /// <summary>
    /// Read quoted field content. Opening quote already consumed.
    /// Uses Span to find the content, only allocates string at the end.
    /// </summary>
    private string ReadQuotedField(bool isLastField)
    {
        // Fast path: check if the entire field is in current buffer without escapes
        int startPos = _bufferPos;
        int endPos = FindQuotedFieldEnd(startPos);
        
        if (endPos >= 0)
        {
            // Fast path - field is complete in buffer, no escapes
            var span = _buffer.AsSpan(startPos, endPos - startPos);
            _bufferPos = endPos + 1; // Skip closing quote
            SkipDelimiter(isLastField);
            return Encoding.UTF8.GetString(span);
        }
        
        // Slow path - field spans buffer boundary or has escapes
        return ReadQuotedFieldSlow(isLastField);
    }

    /// <summary>
    /// Fast scan for end of quoted field. Returns -1 if field has escapes or spans buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindQuotedFieldEnd(int start)
    {
        var span = _buffer.AsSpan(start, _bufferLen - start);
        
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)'"')
            {
                // Check if it's an escaped quote
                if (i + 1 < span.Length && span[i + 1] == (byte)'"')
                {
                    return -1; // Has escapes, use slow path
                }
                return start + i;
            }
            if (b == (byte)'\\')
            {
                return -1; // Has escapes, use slow path
            }
        }
        
        return -1; // Field extends beyond buffer
    }

    /// <summary>
    /// Slow path for quoted fields with escapes or spanning buffers.
    /// </summary>
    private string ReadQuotedFieldSlow(bool isLastField)
    {
        // Use a pooled builder for efficiency
        var sb = new StringBuilder(256);
        
        while (true)
        {
            if (!EnsureData())
            {
                return sb.ToString();
            }
            
            byte b = _buffer[_bufferPos++];
            
            if (b == (byte)'"')
            {
                if (!EnsureData())
                {
                    return sb.ToString();
                }
                
                byte next = _buffer[_bufferPos];
                if (next == (byte)'"')
                {
                    // Escaped quote
                    sb.Append('"');
                    _bufferPos++;
                }
                else
                {
                    // End of field
                    SkipDelimiter(isLastField);
                    return sb.ToString();
                }
            }
            else if (b == (byte)'\\')
            {
                if (!EnsureData())
                {
                    sb.Append('\\');
                    return sb.ToString();
                }
                
                byte escaped = _buffer[_bufferPos];
                switch (escaped)
                {
                    case (byte)'"':
                        sb.Append('"');
                        _bufferPos++;
                        break;
                    case (byte)'\\':
                        sb.Append('\\');
                        _bufferPos++;
                        break;
                    case (byte)'n':
                        sb.Append('\n');
                        _bufferPos++;
                        break;
                    case (byte)'r':
                        sb.Append('\r');
                        _bufferPos++;
                        break;
                    case (byte)'t':
                        sb.Append('\t');
                        _bufferPos++;
                        break;
                    case (byte)'N':
                        sb.Append("\\N");
                        _bufferPos++;
                        break;
                    default:
                        sb.Append('\\');
                        break;
                }
            }
            else
            {
                // Regular byte - could be multi-byte UTF8
                sb.Append((char)b);
            }
        }
    }

    /// <summary>
    /// Read unquoted field content.
    /// </summary>
    private string ReadUnquotedField(bool isLastField, bool includeLeadingBackslash)
    {
        int startPos = _bufferPos;
        
        // Scan for delimiter
        while (_bufferPos < _bufferLen)
        {
            byte b = _buffer[_bufferPos];
            
            if (b == (byte)',' && !isLastField)
            {
                var span = _buffer.AsSpan(startPos, _bufferPos - startPos);
                _bufferPos++; // Skip comma
                var result = Encoding.UTF8.GetString(span);
                return includeLeadingBackslash ? "\\" + result : result;
            }
            if (b == (byte)'\n')
            {
                var span = _buffer.AsSpan(startPos, _bufferPos - startPos);
                _bufferPos++;
                var result = Encoding.UTF8.GetString(span);
                return includeLeadingBackslash ? "\\" + result : result;
            }
            if (b == (byte)'\r')
            {
                var span = _buffer.AsSpan(startPos, _bufferPos - startPos);
                _bufferPos++;
                if (_bufferPos < _bufferLen && _buffer[_bufferPos] == (byte)'\n')
                    _bufferPos++;
                var result = Encoding.UTF8.GetString(span);
                return includeLeadingBackslash ? "\\" + result : result;
            }
            _bufferPos++;
        }
        
        // Need more data - use slow path
        var sb = new StringBuilder();
        if (includeLeadingBackslash) sb.Append('\\');
        sb.Append(Encoding.UTF8.GetString(_buffer.AsSpan(startPos, _bufferLen - startPos)));
        
        while (EnsureData())
        {
            byte b = _buffer[_bufferPos];
            
            if (b == (byte)',' && !isLastField)
            {
                _bufferPos++;
                return sb.ToString();
            }
            if (b == (byte)'\n')
            {
                _bufferPos++;
                return sb.ToString();
            }
            if (b == (byte)'\r')
            {
                _bufferPos++;
                if (EnsureData() && _buffer[_bufferPos] == (byte)'\n')
                    _bufferPos++;
                return sb.ToString();
            }
            sb.Append((char)_buffer[_bufferPos++]);
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// Ensure buffer has data available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EnsureData()
    {
        if (_bufferPos < _bufferLen) return true;
        if (_eof) return false;
        
        return RefillBuffer();
    }

    /// <summary>
    /// Refill the buffer from stream.
    /// </summary>
    private bool RefillBuffer()
    {
        var source = _gzip ?? _stream;
        
        try
        {
            _bufferLen = source.Read(_buffer, 0, _buffer.Length);
            _bufferPos = 0;
            _bytesRead += _bufferLen;
            
            if (_bufferLen == 0)
            {
                _eof = true;
                return false;
            }
            return true;
        }
        catch (InvalidDataException)
        {
            _eof = true;
            return false;
        }
    }

    /// <summary>
    /// Check if first row is header (matches expected column name pattern).
    /// If so, skip it. Returns true if header was skipped.
    /// </summary>
    public bool TrySkipHeader(string firstColumnName)
    {
        // Save position
        int savedPos = _bufferPos;
        int savedLen = _bufferLen;
        
        // Read first field
        if (!EnsureData()) return false;
        
        string? firstField = ReadField(false);
        
        if (firstField == firstColumnName)
        {
            // It's a header - skip rest of row
            for (int i = 1; i < _columnCount; i++)
            {
                SkipField(i == _columnCount - 1);
            }
            _rowNumber = 0;
            return true;
        }
        
        // Not a header - restore position (approximately - we may have refilled buffer)
        // For simplicity, just note that first row was read but not as header
        // Caller should handle this case
        return false;
    }

    public string GetStats()
    {
        return $"Rows: {_rowNumber:N0}, Bytes: {_bytesRead:N0}";
    }

    public void Dispose()
    {
        _gzip?.Dispose();
        _stream.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null!;
    }
}
