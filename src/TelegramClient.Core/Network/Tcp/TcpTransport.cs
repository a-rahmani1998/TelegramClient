﻿namespace TelegramClient.Core.Network.Tcp
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    using log4net;

    using TelegramClient.Core.IoC;
    using TelegramClient.Core.Sessions;
    using TelegramClient.Core.Utils;

    [SingleInstance(typeof(ITcpTransport))]
    internal class TcpTransport : ITcpTransport
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(TcpTransport));

        private readonly ConcurrentQueue<Tuple<byte[], TaskCompletionSource<bool>>> _queue = new ConcurrentQueue<Tuple<byte[], TaskCompletionSource<bool>>>();

        private readonly ManualResetEventSlim _resetEvent = new ManualResetEventSlim(false);

        private int _messageSeqNo;

        public ITcpService TcpService { get; set; }

        public TcpTransport()
        {
            ThreadPool.QueueUserWorkItem(
                state =>
                {
                    while (true)
                    {
                        if (_queue.IsEmpty)
                        {
                            _resetEvent.Reset();
                            _resetEvent.Wait();
                        }

                        _queue.TryDequeue(out var item);

                        try
                        {
                            SendPacket(item.Item1).Wait();
                            item.Item2.SetResult(true);
                        }
                        catch (Exception e)
                        {
                            Log.Error("Process message failed", e);
                        }
                    }
                });
        }

        public Task Send(byte[] packet)
        {
            var tcs = new TaskCompletionSource<bool>();

            PushToQueue(packet, tcs);

            return tcs.Task;
        }

        private async Task SendPacket(byte[] packet)
        {
            var mesSeqNo = _messageSeqNo++;

            Log.Debug($"Send message with seq_no {mesSeqNo}");

            var tcpMessage = new TcpMessage(mesSeqNo, packet);
            var encodedMessage = tcpMessage.Encode();
            await TcpService.Send(encodedMessage).ConfigureAwait(false);
        }

        private void PushToQueue(byte[] packet, TaskCompletionSource<bool> tcs)
        {
            _queue.Enqueue(Tuple.Create(packet, tcs) );

            if (!_resetEvent.IsSet)
            {
                _resetEvent.Set();
            }
        }

        public async Task<byte[]> Receieve()
        {
            var stream = await TcpService.Receieve().ConfigureAwait(false);

            var packetLengthBytes = new byte[4];
            var readLenghtBytes = await stream.ReadAsync(packetLengthBytes, 0, 4).ConfigureAwait(false);

            if (readLenghtBytes != 4)
                throw new InvalidOperationException("Couldn't read the packet length");

            var packetLength = BitConverter.ToInt32(packetLengthBytes, 0);

            var seqBytes = new byte[4];
            var readSeqBytes = await stream.ReadAsync(seqBytes, 0, 4).ConfigureAwait(false);

            if (readSeqBytes != 4)
                throw new InvalidOperationException("Couldn't read the sequence");
            var mesSeqNo = BitConverter.ToInt32(seqBytes, 0);

            Log.Debug($"Recieve message with seq_no {mesSeqNo}");

            var readBytes = 0;
            var body = new byte[packetLength - 12];
            var neededToRead = packetLength - 12;

            do
            {
                var bodyByte = new byte[packetLength - 12];
                var availableBytes = await stream.ReadAsync(bodyByte, 0, neededToRead).ConfigureAwait(false);
                neededToRead -= availableBytes;
                Buffer.BlockCopy(bodyByte, 0, body, readBytes, availableBytes);
                readBytes += availableBytes;
            }
            while (readBytes != packetLength - 12);

            var crcBytes = new byte[4];
            var readCrcBytes = await stream.ReadAsync(crcBytes, 0, 4).ConfigureAwait(false);
            if (readCrcBytes != 4)
                throw new InvalidOperationException("Couldn't read the crc");
            var checksum = BitConverter.ToInt32(crcBytes, 0);

            var rv = new byte[packetLengthBytes.Length + seqBytes.Length + body.Length];

            Buffer.BlockCopy(packetLengthBytes, 0, rv, 0, packetLengthBytes.Length);
            Buffer.BlockCopy(seqBytes, 0, rv, packetLengthBytes.Length, seqBytes.Length);
            Buffer.BlockCopy(body, 0, rv, packetLengthBytes.Length + seqBytes.Length, body.Length);
            var crc32 = new Crc32();
            crc32.SlurpBlock(rv, 0, rv.Length);
            var validChecksum = crc32.Crc32Result;

            if (checksum != validChecksum)
                throw new InvalidOperationException("invalid checksum! skip");

            return body;
        }
    }
}