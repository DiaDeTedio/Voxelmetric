﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Assertions;
using Voxelmetric.Code.Common;
using Voxelmetric.Code.Data_types;
using Voxelmetric.Code.Utilities;
using Voxelmetric.Code.VM;

namespace Voxelmetric.Code.Core
{
    public sealed class ChunkBlocks
    {
        private Chunk chunk;
        private readonly Block[] blocks = Helpers.CreateArray1D<Block>(Env.ChunkVolume);
        private byte[] receiveBuffer;
        private int receiveIndex;

        public readonly List<BlockPos> modifiedBlocks = new List<BlockPos>();
        public bool contentsModified;

        private static byte[] emptyBytes;

        public static byte[] EmptyBytes
        {
            get
            {
                if (emptyBytes==null)
                    emptyBytes = new byte[16384]; // TODO: Validate whether this is fine
                return emptyBytes;
            }
        }

        public ChunkBlocks(Chunk chunk)
        {
            this.chunk = chunk;
        }

        public Block this[int x, int y, int z]
        {
            get
            {
                int index = Helpers.GetChunkIndex1DFrom3D(x, y, z);
                return blocks[index];
            }
            set
            {
                int index = Helpers.GetChunkIndex1DFrom3D(x, y, z);
                blocks[index] = value;
            }
        }

        public Block this[int index]
        {
            get
            {
                Assert.IsTrue(index>0 && index<=Env.ChunkVolume);
                return blocks[index];
            }
            set
            {
                Assert.IsTrue(index>0 && index<=Env.ChunkVolume);
                blocks[index] = value;
            }
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("contentsModified=");
            sb.Append(contentsModified.ToString());
            return sb.ToString();
        }

        public void Reset()
        {
            Array.Clear(blocks, 0, blocks.Length);
            contentsModified = false;
            modifiedBlocks.Clear();
        }

        /// <summary>
        /// Gets and returns a block from a position within the chunk or fetches it from the world
        /// </summary>
        /// <param name="blockPos">A global block position</param>
        /// <returns>The block at the position</returns>
        public Block Get(BlockPos blockPos)
        {
            if (InRange(blockPos))
                return LocalGet(blockPos-chunk.pos);

            return chunk.world.blocks.Get(blockPos);
        }

        /// <summary>
        /// This function takes a block position relative to the chunk's position. It is slightly faster
        /// than the GetBlock function so use this if you already have a local position available otherwise
        /// use GetBlock. If the position is lesser or greater than the size of the chunk it will get the value
        /// from the chunk containing the block pos
        /// </summary>
        /// <param name="localBlockPos"> A block pos relative to the chunk's position. MUST be a local position or the wrong block will be returned</param>
        /// <returns>the block at the relative position</returns>
        public Block LocalGet(BlockPos localBlockPos)
        {
            if ((localBlockPos.x<Env.ChunkSize && localBlockPos.x>=0) &&
                (localBlockPos.y<Env.ChunkSize && localBlockPos.y>=0) &&
                (localBlockPos.z<Env.ChunkSize && localBlockPos.z>=0))
            {
                Block block = this[localBlockPos.x, localBlockPos.y, localBlockPos.z];
                return block ?? chunk.world.Air;
            }

            return chunk.world.blocks.Get(localBlockPos+chunk.pos);
        }

        public void Set(BlockPos blockPos, string block, bool updateChunk = true, bool setBlockModified = true)
        {
            Set(blockPos, Block.Create(block, chunk.world), updateChunk, setBlockModified);
        }

        /// <summary> Sets the block at the given position </summary>
        /// <param name="blockPos">Block position</param>
        /// <param name="newBlock">Block to place at the given location</param>
        /// <param name="updateChunk">Optional parameter, set to false to keep the chunk unupdated despite the change</param>
        /// <param name="setBlockModified">Optional parameter, set to true to mark chunk data as modified</param>
        public void Set(BlockPos blockPos, Block newBlock, bool updateChunk = true, bool setBlockModified = true)
        {
            if (InRange(blockPos))
            {
                //Only call create and destroy if this is a different block type, otherwise it's just updating the properties of an existing block
                Block block = Get(blockPos);
                if (block.type!=newBlock.type)
                {
                    block.OnDestroy(chunk, blockPos, blockPos+chunk.pos);
                    newBlock.OnCreate(chunk, blockPos, blockPos+chunk.pos);
                }

                this[blockPos.x-chunk.pos.x, blockPos.y-chunk.pos.y, blockPos.z-chunk.pos.z] = newBlock;

                if (setBlockModified)
                    BlockModified(blockPos);

                if (updateChunk)
                    chunk.RequestBuildVertices();
            }
            else
            {
                //if the block is out of range set it through world
                chunk.world.blocks.Set(blockPos, newBlock, updateChunk);
            }
        }

        /// <summary>
        /// This function takes a block position relative to the chunk's position. It is slightly faster
        /// than the SetBlock function so use this if you already have a local position available otherwise
        /// use SetBlock. If the position is lesser or greater than the size of the chunk it will call setblock
        /// using the world.
        /// </summary>
        /// <param name="blockPos"> A block pos relative to the chunk's position.</param>
        /// <param name="block">Block to place at the given location</param>
        public void LocalSet(BlockPos blockPos, Block block)
        {
            if ((blockPos.x<Env.ChunkSize && blockPos.x>=0) &&
                (blockPos.y<Env.ChunkSize && blockPos.y>=0) &&
                (blockPos.z<Env.ChunkSize && blockPos.z>=0))
            {
                this[blockPos.x, blockPos.y, blockPos.z] = block;
            }
        }

        /// <summary> Returns true if the block local block position is contained in the chunk boundaries </summary>
        /// <param name="blockPos">A block position</param>
        public bool InRange(BlockPos blockPos)
        {
            return (blockPos.ContainingChunkCoordinates()==chunk.pos);
        }

        public void BlockModified(BlockPos pos)
        {
            //If this is the server log the changed block so that it can be saved
            if (chunk.world.networking.isServer)
            {
                if (chunk.world.networking.allowConnections)
                {
                    chunk.world.networking.server.BroadcastChange(pos, Get(pos), -1);
                }

                if (!modifiedBlocks.Contains(pos))
                {
                    modifiedBlocks.Add(pos);
                    chunk.blocks.contentsModified = true;
                }
            }
            else // if this is not the server send the change to the server to sync
            {
                chunk.world.networking.client.BroadcastChange(pos, Get(pos));
            }
        }

        private bool debugRecieve = false;

        private void InitializeChunkDataReceive(int index, int size)
        {
            receiveIndex = index;
            receiveBuffer = new byte[size];
        }

        public void ReceiveChunkData(byte[] buffer)
        {
            int index = BitConverter.ToInt32(buffer, VmServer.headerSize);
            int size = BitConverter.ToInt32(buffer, VmServer.headerSize+4);
            if (debugRecieve)
                Debug.Log("ChunkBlocks.ReceiveChunkData ("+Thread.CurrentThread.ManagedThreadId+"): "+chunk.pos
                          //+ ", buffer=" + buffer.Length
                          +", index="+index
                          +", size="+size);

            if (receiveBuffer==null)
                InitializeChunkDataReceive(index, size);
            TranscribeChunkData(buffer, VmServer.leaderSize);
        }

        private void TranscribeChunkData(byte[] buffer, int offset)
        {
            for (int o = offset; o<buffer.Length; o++)
            {
                receiveBuffer[receiveIndex] = buffer[o];
                receiveIndex++;

                if (receiveIndex==receiveBuffer.Length)
                {
                    if (debugRecieve)
                        Debug.Log("ChunkBlocks.TranscribeChunkData ("+Thread.CurrentThread.ManagedThreadId+"): "+
                                  chunk.pos
                                  +", receiveIndex="+receiveIndex);

                    FinishChunkDataReceive();
                    return;
                }
            }
        }

        private void FinishChunkDataReceive()
        {
            GenerateContentsFromBytes();

            Chunk.OnGenerateDataOverNetworkDone(chunk);

            receiveBuffer = null;
            receiveIndex = 0;

            if (debugRecieve)
                Debug.Log("ChunkBlocks.FinishChunkDataReceive ("+Thread.CurrentThread.ManagedThreadId+"): "+chunk.pos);
        }

        public byte[] ToBytes()
        {
            List<byte> buffer = new List<byte>();
            Block block;
            Block lastBlock = null;

            byte[] blockData;
            short sameBlockCount = 1000;
            int countIndex = 0;

            for (int y = 0; y<Env.ChunkSize; y++)
            {
                for (int z = 0; z<Env.ChunkSize; z++)
                {
                    for (int x = 0; x<Env.ChunkSize; x++)
                    {
                        block = LocalGet(new BlockPos(x, y, z));
                        if (block.Equals(lastBlock))
                        {
                            //if this is the same as the last block added increase the count
                            sameBlockCount++;
                            byte[] shortAsBytes = BitConverter.GetBytes(sameBlockCount);
                            buffer[countIndex] = shortAsBytes[0];
                            buffer[countIndex+1] = shortAsBytes[1];
                        }
                        else
                        {
                            blockData = block.ToByteArray();

                            //Add 1 as a short (2 bytes) 
                            countIndex = buffer.Count;
                            sameBlockCount = 1;
                            buffer.AddRange(BitConverter.GetBytes(1));
                            //Then add the block data
                            buffer.AddRange(blockData);
                            lastBlock = block;
                        }

                    }
                }
            }

            return buffer.ToArray();
        }

        private void GenerateContentsFromBytes()
        {
            int i = 0;
            Block block = null;
            short blockCount = 0;

            for (int y = 0; y<Env.ChunkSize; y++)
            {
                for (int z = 0; z<Env.ChunkSize; z++)
                {
                    for (int x = 0; x<Env.ChunkSize; x++)
                    {
                        if (blockCount==0)
                        {
                            blockCount = BitConverter.ToInt16(receiveBuffer, i);
                            i += 2;

                            block = Block.Create(BitConverter.ToUInt16(receiveBuffer, i), chunk.world);
                            i += 2;
                            i += block.RestoreBlockData(receiveBuffer, i);

                        }

                        LocalSet(new BlockPos(x, y, z), block);
                        blockCount--;
                    }
                }
            }
        }
    }
}