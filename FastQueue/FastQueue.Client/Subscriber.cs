﻿using FastQueue.Client.Abstractions;
using FastQueue.Client.Exceptions;
using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastQueue.Client
{
    public class Subscriber : ISubscriber
    {
        private readonly AsyncDuplexStreamingCall<FastQueueService.CompleteRequest, FastQueueService.Messages> duplexStream;
        private readonly Action<ISubscriber, IEnumerable<Message>> messagesHandler;
        private readonly IClientStreamWriter<FastQueueService.CompleteRequest> requestStream;
        private readonly IAsyncStreamReader<FastQueueService.Messages> responseStream;
        private CancellationTokenSource cancellationTokenSource;
        private Task<Task> receivingLoopTask;
        private bool disposed = false;

        internal Subscriber(Grpc.Core.AsyncDuplexStreamingCall<FastQueueService.CompleteRequest, FastQueueService.Messages> duplexStream,
            Action<ISubscriber, IEnumerable<Message>> messagesHandler)
        {
            this.duplexStream = duplexStream;
            this.messagesHandler = messagesHandler;
            requestStream = duplexStream.RequestStream;
            responseStream = duplexStream.ResponseStream;
            cancellationTokenSource = new CancellationTokenSource();
        }

        internal void StartReceivingLoop()
        {
            receivingLoopTask = Task.Factory.StartNew(() => ReceivingLoop(cancellationTokenSource.Token), TaskCreationOptions.LongRunning);
        }

        private async Task ReceivingLoop(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var messages in responseStream.ReadAllAsync(cancellationToken))
                {
                    // ::: change ToByteArray on Memory when available
                    var receivedMessages = messages.Messages_.Select(x => new Message(x.Id, new DateTime(x.Timestamp), new ReadOnlyMemory<byte>(x.Body.ToByteArray())));
                    messagesHandler?.Invoke(this, receivedMessages);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
            }
            catch
            {
                TaskHelper.FireAndForget(async () => await DisposeAsync());
                throw;
            }
        }

        public async Task Complete(long messageId)
        {
            if (disposed)
            {
                throw new SubscriberException($"Cannot write to disposed {nameof(Subscriber)}");
            }

            await requestStream.WriteAsync(new FastQueueService.CompleteRequest
            {
                Id = messageId
            });
        }

        public async ValueTask DisposeAsync()
        {
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            cancellationTokenSource.Cancel();
            await await receivingLoopTask;

            await requestStream.CompleteAsync();

            duplexStream?.Dispose();
        }
    }
}
