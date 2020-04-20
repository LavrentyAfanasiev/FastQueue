﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FastQueue.Server.Core
{
    internal class InfiniteArray<T>
    {
        private const int ListCapacity = 128;
        private const int MinimumFreeBlocks = 2;

        private readonly int blockLength;
        private long offset;
        private object sync = new object();
        private List<T[]> data;
        private int firstFreeBlockIndex;
        private int firstBusyBlockIndex;
        private int firstItemIndexInBlock;
        private int firstFreeIndexInBlock;

        public InfiniteArray(int blockLength, long initialOffset)
        {
            this.blockLength = blockLength;
            offset = initialOffset;
            data = new List<T[]>(ListCapacity);
            data[0] = new T[blockLength];
            firstFreeBlockIndex = 0;
            firstBusyBlockIndex = 0;
            firstItemIndexInBlock = 0;
            firstFreeIndexInBlock = 0;
        }

        public void Add(Span<T> items)
        {
            if (items.Length == 0)
            {
                return;
            }

            lock (sync)
            {
                if (items.Length <= blockLength - firstFreeIndexInBlock)
                {
                    // if block contains enough space just copy the data
                    items.CopyTo(data[^1].AsSpan(firstFreeIndexInBlock));
                    firstFreeIndexInBlock += items.Length;
                }
                else
                {
                    items.Slice(0, blockLength - firstFreeIndexInBlock).CopyTo(data[^1].AsSpan(firstFreeIndexInBlock));
                    var sourceInd = blockLength - firstFreeIndexInBlock;
                    while (sourceInd + blockLength <= items.Length)
                    {
                        StartNewBlock();
                        items.Slice(sourceInd, blockLength).CopyTo(data[^1].AsSpan());
                        sourceInd += blockLength;
                    }

                    if (sourceInd < items.Length)
                    {
                        StartNewBlock();
                        items.Slice(sourceInd).CopyTo(data[^1].AsSpan());
                        firstFreeIndexInBlock = items.Length - sourceInd;
                    }
                    else
                    {
                        firstFreeIndexInBlock = blockLength;
                    }

                    CheckForCleanUp();
                }
            }
        }

        public void Add(T item)
        {
            lock (sync)
            {
                if (firstFreeIndexInBlock < blockLength)
                {
                    data[^1][firstFreeIndexInBlock++] = item;
                }
                else
                {
                    StartNewBlock();
                    data[^1][firstFreeIndexInBlock = 0] = item;
                    CheckForCleanUp();
                }
            }
        }

        public void FreeTo(long index)
        {
            if (index < 0)
            {
                throw new IndexOutOfRangeException($"Index must be greater then 0. Index: {index}");
            }

            lock (sync)
            {
                var blockInd = GetBlockIndex(index);
                var indInBlock = GetIndexInBlock(index);

                if (blockInd < firstBusyBlockIndex
                    || (blockInd == firstBusyBlockIndex && indInBlock <= firstItemIndexInBlock))
                {
                    return;
                }

                if (blockInd >= data.Count
                    || (blockInd == (data.Count - 1) && indInBlock > firstFreeIndexInBlock))
                {
                    throw new IndexOutOfRangeException($"Item with index {index} doesn't exist in the array");
                }

                firstBusyBlockIndex = blockInd;
                firstItemIndexInBlock = indInBlock;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StartNewBlock()
        {
            if (firstBusyBlockIndex - firstFreeBlockIndex > 0)
            {
                // if we have free block reuse it
                data.Add(data[firstFreeBlockIndex]);
                data[firstFreeBlockIndex] = null;
                firstFreeBlockIndex++;
            }
            else
            {
                // allocate new block
                data.Add(new T[blockLength]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CheckForCleanUp()
        {
            var busyBlocks = data.Count - firstBusyBlockIndex;
            var freeBlocks = firstBusyBlockIndex - firstFreeBlockIndex;

            if (freeBlocks > (busyBlocks / 2) && freeBlocks > MinimumFreeBlocks)
            {
                for (int i = firstFreeBlockIndex; i < firstBusyBlockIndex - MinimumFreeBlocks; i++)
                {
                    data[i] = null;
                }

                firstFreeBlockIndex = firstBusyBlockIndex - MinimumFreeBlocks;
            }

            var newBlocksTotal = data.Count - firstFreeBlockIndex;
            if (data.Count >= ListCapacity && newBlocksTotal < (data.Count / 2))
            {
                var newData = new List<T[]>(Math.Max(ListCapacity, newBlocksTotal));
                for (int i = firstFreeBlockIndex; i < data.Count; i++)
                {
                    newData[i - firstFreeBlockIndex] = data[i];
                }

                data = newData;
                firstBusyBlockIndex -= firstFreeBlockIndex;
                offset += firstFreeBlockIndex * blockLength;
                firstFreeBlockIndex = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long GetFirstItemIndex() => offset + firstBusyBlockIndex * blockLength + firstItemIndexInBlock;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long GetLastItemIndex() => offset + (data.Count - 1) * blockLength + firstFreeIndexInBlock - 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetBlockIndex(long index) => checked((int)((index - offset) / blockLength));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetIndexInBlock(long index) => checked((int)((index - offset) % blockLength));
    }
}
