namespace TelegramClient.Core.Network.Recieve
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using log4net;

    using Newtonsoft.Json;

    using OpenTl.Schema;
    using OpenTl.Schema.Serialization;

    using TelegramClient.Core.Helpers;
    using TelegramClient.Core.IoC;
    using TelegramClient.Core.MTProto.Crypto;
    using TelegramClient.Core.Network.Confirm;
    using TelegramClient.Core.Network.Recieve.Interfaces;
    using TelegramClient.Core.Network.RecieveHandlers.Interfaces;
    using TelegramClient.Core.Network.Tcp;
    using TelegramClient.Core.Settings;
    using TelegramClient.Core.Utils;

    [SingleInstance(typeof(IRecievingService))]
    internal class RecievingService : IRecievingService
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(RecievingService));

        private CancellationTokenSource _recievingCts;

        public ITcpTransport TcpTransport { get; set; }

        public IClientSettings ClientSettings { get; set; }

        public IConfirmationSendService ConfirmationSendService { get; set; }

        public Dictionary<Type, IRecieveHandler> RecieveHandlersMap { get; set; }

        public IGZipPackedHandler ZipPackedHandler { get; set; }

        public void StartReceiving()
        {
            if (_recievingCts != null && _recievingCts.IsCancellationRequested)
            {
                return;
            }

            _recievingCts = new CancellationTokenSource();

            Task.Run(
                () =>
                {
                    while (!_recievingCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var recieveTask = TcpTransport.Receieve();
                            recieveTask.Wait(_recievingCts.Token);
                            var recieveData = recieveTask.Result;

                            var decodedData = DecodeMessage(recieveData);

                            Log.Debug($"Recieve message with remote id: {decodedData.Item2}");

                            ProcessReceivedMessage(decodedData.Item1);

                            ConfirmationSendService.AddForSend(decodedData.Item2);
                        }
                        catch (Exception e)
                        {
                            Log.Error("Recieve message failed", e);
                        }
                    }
                });
        }

        public void StopRecieving()
        {
            _recievingCts?.Cancel();
        }

        private Tuple<byte[], long> DecodeMessage(byte[] body)
        {
            byte[] message;
            long remoteMessageId;

            using (var inputStream = new MemoryStream(body))
            using (var inputReader = new BinaryReader(inputStream))
            {
                if (inputReader.BaseStream.Length < 8)
                {
                    throw new InvalidOperationException("Can\'t decode packet");
                }

                var remoteAuthKeyId = inputReader.ReadUInt64(); // TODO: check auth key id
                var msgKey = inputReader.ReadBytes(16); // TODO: check msg_key correctness
                var keyData = TlHelpers.CalcKey(ClientSettings.Session.AuthKey.Data, msgKey, false);

                var plaintext = AES.DecryptAes(
                    keyData,
                    inputReader.ReadBytes((int)(inputStream.Length - inputStream.Position)));

                using (var plaintextStream = new MemoryStream(plaintext))
                using (var plaintextReader = new BinaryReader(plaintextStream))
                {
                    var remoteSalt = plaintextReader.ReadUInt64();
                    var remoteSessionId = plaintextReader.ReadUInt64();
                    remoteMessageId = plaintextReader.ReadInt64();
                    plaintextReader.ReadInt32();
                    var msgLen = plaintextReader.ReadInt32();
                    message = plaintextReader.ReadBytes(msgLen);
                }
            }

            return Tuple.Create(message, remoteMessageId);
        }

        private void ProcessReceivedMessage(byte[] message)
        {
            var obj = Serializer.DeserializeObject(message);

            ProcessReceivedMessage(obj);
        }

        private void ProcessReceivedMessage(IObject obj)
        {
            if (Log.IsDebugEnabled)
            {
                var jObject = JsonConvert.SerializeObject(obj);
                Log.Debug($"Try handle response for object: {obj} \n{jObject}");
            }

            switch (obj)
            {
                case var o when RecieveHandlersMap.TryGetValue(o.GetType(), out var handler):
                    Log.Debug($"Handler found - {handler}");
                    handler.HandleResponce(obj);
                    break;
                case TMsgContainer container:
                    foreach (var containerMessage in container.Messages)
                    {
                        ProcessReceivedMessage(containerMessage.Body);
                        ConfirmationSendService.AddForSend(containerMessage.MsgId);
                    }
                    break;
                case TgZipPacked zipPacked:
                    var unzippedData = ZipPackedHandler.HandleGZipPacked(zipPacked);
                    ProcessReceivedMessage(unzippedData);
                    break;
                default:
                    if (Log.IsErrorEnabled)
                    {
                        var jObject = JsonConvert.SerializeObject(obj);
                        Log.Error($"Cannot handle object: {obj} \n{jObject}");
                    }
                    break;
            }
        }
    }
}