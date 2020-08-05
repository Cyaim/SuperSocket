using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Text;
using SuperSocket.ProtoBase;

namespace SuperSocket.WebSocket.Extensions.Compression
{
    /// <summary>
    /// WebSocket Per-Message Compression Extension
    /// https://tools.ietf.org/html/rfc7692
    /// </summary>
    public class WebSocketPerMessageCompressionExtension : IWebSocketExtension
    {
        public string Name => PMCE;

        public const string PMCE = "permessage-deflate";

        private const int _deflateBufferSize = 1024 * 1024 * 4;

        private static readonly Encoding _encoding = new UTF8Encoding(false);

        private static readonly byte[] LAST_FOUR_OCTETS = new byte[] { 0x00, 0x00, 0xFF, 0xFF };
        private static readonly byte[] LAST_FOUR_OCTETS_REVERSE = new byte[] { 0xFF, 0xFF, 0x00, 0x00 };
        private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;

        public void Decode(WebSocketPackage package)
        {
            if (!package.RSV1)
                return;

            var data = package.Data;
            data = data.ConcactSequence(new SequenceSegment(LAST_FOUR_OCTETS_REVERSE, LAST_FOUR_OCTETS_REVERSE.Length, false));

            SequenceSegment head = null;
            SequenceSegment tail = null;

            using (var stream = new DeflateStream(new ReadOnlySequenceStream(data), CompressionMode.Decompress))
            {
                while (true)
                {
                    var buffer = _arrayPool.Rent(_deflateBufferSize);
                    var read = stream.Read(buffer, 0, buffer.Length);

                    if (read == 0)
                        break;

                    var segment = new SequenceSegment(buffer, read);

                    if (head == null)
                        tail = head = segment;
                    else
                        tail.SetNext(segment);
                }
            }

            data = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        }

        public void Encode(WebSocketMessage message)
        {
            message.RSV1 = true;

            if (message.Data.IsEmpty)
                EncodeTextMessage(message);
            else
                EncodeDataMessage(message);            
        }

        private void EncodeTextMessage(WebSocketMessage message)
        {
            var encoder = _encoding.GetEncoder();
            var text = message.Message.AsSpan();
            var completed = false;      

            SequenceSegment head = null;
            SequenceSegment tail = null; 

            using (var stream = new DeflateStream(new MemoryStream(), CompressionMode.Decompress))
            {
                while (!completed)
                {
                    var buffer = _arrayPool.Rent(_deflateBufferSize);
                    Span<byte> span = buffer;

                    encoder.Convert(text, span, false, out int charsUsed, out int bytesUsed, out completed);
                
                    if (charsUsed > 0)
                        text = text.Slice(charsUsed);

                    stream.Write(buffer, 0, bytesUsed);
                    stream.Flush();

                    var read = stream.Read(buffer, 0, buffer.Length);

                    if (read == 0)
                        continue;

                    var segment = new SequenceSegment(buffer, read);

                    if (head == null)
                        tail = head = segment;
                    else
                        tail.SetNext(segment);
                }
            }

            message.Data = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        }

        private void RemoveLastFourOctets(ref ReadOnlySequence<byte> data)
        {
            var octetsLen = LAST_FOUR_OCTETS_REVERSE.Length;

            if (data.Length < octetsLen)
                return;

            var lastFourBytes = data.Slice(data.Length - octetsLen, octetsLen);
            var pos = 0;

            foreach (var piece in lastFourBytes)
            {
                for (var i = 0; i < piece.Length; i++)
                {
                    if (piece.Span[i] != LAST_FOUR_OCTETS_REVERSE[pos++])
                        return;
                }
            }

            data = data.Slice(0, data.Length - octetsLen);
        }

        private void EncodeDataMessage(WebSocketMessage message)
        {
            var data = message.Data;

            RemoveLastFourOctets(ref data);

            SequenceSegment head = null;
            SequenceSegment tail = null;

            using (var stream = new DeflateStream(new ReadOnlySequenceStream(data), CompressionMode.Compress))
            {
                while (true)
                {
                    var buffer = _arrayPool.Rent(_deflateBufferSize);
                    var read = stream.Read(buffer, 0, buffer.Length);

                    if (read == 0)
                        break;

                    var segment = new SequenceSegment(buffer, read);

                    if (head == null)
                        tail = head = segment;
                    else
                        tail.SetNext(segment);
                }
            }

            data = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        }
    }
}
