﻿using FastQueue.Server.Core;
using FastQueue.Server.Core.Abstractions;
using FastQueue.Server.Core.Model;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Schema;

namespace TestConsole
{
    class Program
    {
        private static Server server;

        static async Task Main(string[] args)
        {
            //await InfiniteArrayTest();
            //await TopicPerformance();
            //await TopicTest();
            await ServerTest();

            Console.WriteLine("end");
            await Task.CompletedTask;
        }

        static async Task ServerTest()
        {
            var topicFactory = new FileTopicFactory(new TopicFactoryOptions
            {
                DirectoryPath = @"C:\temp\storage",
                PersistentStorageFileLengthThreshold = 100 * 1024 * 1024,
                SubscriptionPointersStorageFileLengthThreshold = 10 * 1024 * 1024,
                TopicOptions = new TopicOptions
                {
                    PersistenceIntervalMilliseconds = 50,
                    DataArrayOptions = new InfiniteArrayOptions
                    {
                        BlockLength = 100000,
                        DataListCapacity = 128,
                        MinimumFreeBlocks = 20
                    }
                }
            });

            var topicsConfigStorage = new TopicsConfigurationFileStorage(new TopicsConfigurationFileStorageOptions
            {
                ConfigurationFile = @"C:\temp\storage\Topics.json"
            });

            server = new Server(topicFactory, topicsConfigStorage);
            server.Restore();

            string topicName = "topic1";

            //server.CreateNewTopic(topicName);

            var writerTask = Task.Factory.StartNew(() => WriterLoop(topicName), TaskCreationOptions.LongRunning);

            Console.WriteLine("Before Read");

            //server.GetTopic(topicName).DeleteSubscription("sub2");
            //server.GetTopic(topicName).DeleteSubscription("sub3");

            var sub1 = Read(topicName, "sub1");
            //var sub2 = Read(topicName, "sub2");
            //var sub3 = Read(topicName, "sub3");

            Console.WriteLine("Before WhenAll");

            await await writerTask;

            Console.WriteLine("After WhenAll");

            sub1.Dispose();
            //sub2.Dispose();
            //sub3.Dispose();

            server.Stop();
        }

        static async Task WriterLoop(string topicName)
        {
            var rnd = new Random(DateTimeOffset.UtcNow.Millisecond);
            var messages = new byte[1000][];
            for (int i = 0; i < messages.Length; i++)
            {
                byte[] arr = new byte[100];
                rnd.NextBytes(arr);
                messages[i] = arr;
            }

            var topic = server.GetTopic(topicName);

            var writer = topic.CreateWriter(async (ack, c) =>
            {
                Console.WriteLine($"Confirmed {ack.SequenceNumber}. {DateTimeOffset.UtcNow:mm:ss.fffffff}");
                await Task.CompletedTask;
            });

            for (int i = 0; i < 1_000_000; i++)
            {
                var m = messages[i % messages.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(DateTime.UtcNow.Ticks), 0, m, 0, 8);
                writer.Write(new WriteRequest(i, m));
            }

            await Task.Delay(2000);

            writer.Dispose();
        }

        static ISubscriber Read(string topicName, string subName)
        {
            var topic = server.GetTopic(topicName);
            if (!topic.SubscriptionExists(subName))
            {
                topic.CreateSubscription(subName);
            }

            int receivedCount = 0;
            long prevId = 0;
            ISubscriber sub = null;
            sub = topic.Subscribe(subName, async (ms, ct) =>
            {
                var cnt = Interlocked.Add(ref receivedCount, ms.Length);
                Console.WriteLine($"{subName}: Received {cnt}. Last {ms.Span[^1].ID} {DateTimeOffset.UtcNow:mm:ss.fffffff}");

                void AppendPathSeparator(ReadOnlySpan<Message> msgs)
                {
                    for (int i = 0; i < msgs.Length; i++)
                    {
                        if (prevId > 0 && msgs[i].ID - 1 != prevId)
                        {
                            Console.WriteLine($"{subName}: Missing {prevId} - {msgs[i].ID}. {DateTimeOffset.UtcNow:mm:ss.fffffff}");
                        }

                        prevId = msgs[i].ID;
                    }

                    sub.Complete(msgs[^1].ID);

                    var d = new DateTimeOffset(BitConverter.ToInt64(msgs[0].Body.Span.Slice(0, 8)), TimeSpan.Zero);

                    Console.WriteLine($"{subName}: Max latency: {(DateTimeOffset.UtcNow - d).TotalMilliseconds}");
                }

                AppendPathSeparator(ms.Span);

                await Task.CompletedTask;
            }, new SubscriberOptions
            {
                MaxMessagesInBatch = 10000,
                PushIntervalMilliseconds = 50
            });

            return sub;
        }

        static async Task TopicTest()
        {
            var storage = new FilePersistentStorage(new FilePersistentStorageOptions
            {
                FileLengthThreshold = 100 * 1024 * 1024,
                DirectoryPath = @"C:\temp\storage",
                NamePrefix = "Data"
            });

            var subConfigStorage = new SubscriptionsConfigurationFileStorage(new SubscriptionsConfigurationFileStorageOptions
            {
                DirectoryPath = @"C:\temp\storage"
            });

            var subPointersStorage = new SubscriptionPointersFileStorage(new SubscriptionPointersFileStorageOptions
            {
                FileLengthThreshold = 10 * 1024,
                DirectoryPath = @"C:\temp\storage",
                NamePrefix = "Pointers"
            });

            var topic = new Topic("test", storage, subConfigStorage, subPointersStorage, new TopicOptions
            {
                PersistenceIntervalMilliseconds = 100,
                DataArrayOptions = new InfiniteArrayOptions
                {
                    BlockLength = 100000,
                    DataListCapacity = 128,
                    MinimumFreeBlocks = 20
                }
            });

            topic.Restore();

            topic.Start();

            int receivedCount = 0;
            long prevId = topic.PersistedMessageId;
            //topic.CreateSubscription("sub1");
            ISubscriber sub = null;
            sub = topic.Subscribe("sub1", async (ms, ct) =>
            {
                var cnt = Interlocked.Add(ref receivedCount, ms.Length);
                Console.WriteLine($"Received {cnt}. Last {ms.Span[^1].ID} {DateTimeOffset.UtcNow:mm:ss.fffffff}");

                var arr = ms.ToArray();
                for (int i = 0; i < ms.Length; i++)
                {
                    if (arr[i].ID - 1 != prevId)
                    {
                        Console.WriteLine($"Missing {prevId} - {arr[i].ID}. {DateTimeOffset.UtcNow:mm:ss.fffffff}");
                    }

                    prevId = arr[i].ID;
                }

                sub.Complete(arr[^1].ID);

                await Task.CompletedTask;
            }, new SubscriberOptions
            {
                MaxMessagesInBatch = 10000,
                PushIntervalMilliseconds = 50
            });

            var writer = topic.CreateWriter(async (ack, ct) =>
            {
                Console.WriteLine($"Confirmed {ack.SequenceNumber}. {DateTimeOffset.UtcNow:mm:ss.fffffff}");
                await Task.CompletedTask;
            });

            long seqNum = 0;
            for (int i = 0; i < 10; i++)
            {
                writer.Write(new WriteRequest(seqNum++, new byte[] { 1, 2, 3, (byte)i }));
            }
            await Task.Delay(500);


            byte[] buffer = new byte[1000000];
            new Random(DateTimeOffset.UtcNow.Millisecond).NextBytes(buffer);
            var messages = new ReadOnlyMemory<byte>[1000];

            int start = 0;
            int length = 100;
            for (int i = 0; i < messages.Length; i++)
            {
                if (start + length > messages.Length)
                {
                    start = 0;
                }

                messages[i] = buffer.AsMemory(start, length);
                start += length;
            }

            start = 0;
            length = 1;
            Console.WriteLine($"Start sending: {DateTimeOffset.UtcNow:mm:ss.fffffff}");

            for (long i = 0; i < 100_000_000; i += length)
            {
                if (start + length > messages.Length)
                {
                    start = 0;
                }

                //writer.Write(new WriteManyRequest(seqNum, messages.AsMemory(start, length)));
                writer.Write(new WriteRequest(seqNum, messages[start]));

                seqNum += length;

                start += length;
            }

            Console.WriteLine($"Stop sending: {DateTimeOffset.UtcNow:mm:ss.fffffff}");

            await Task.Delay(2000);
        }

        static async Task InfiniteArrayTest()
        {
            var data = new InfiniteArray<long>(0, new InfiniteArrayOptions
            {
                BlockLength = 10,
                DataListCapacity = 5,
                MinimumFreeBlocks = 4
            });

            for (long i = 0; i < 127; i++)
            {
                data.Add(i);

                if (i > 8 && i % 5 == 0)
                {
                    data.FreeTo(i - 8);
                }
            }

            data.FreeTo(124);


            var blocks = data.GetDataBlocks();
        }
    }
}
