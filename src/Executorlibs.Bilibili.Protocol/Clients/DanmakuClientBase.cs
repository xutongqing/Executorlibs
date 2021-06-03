using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Executorlibs.Bilibili.Protocol.Invokers;
using Executorlibs.Bilibili.Protocol.Models.Danmaku;
using Executorlibs.Bilibili.Protocol.Models.General;
using Executorlibs.Bilibili.Protocol.Options;
using Executorlibs.Bilibili.Protocol.Services;
using Microsoft.Extensions.Options;

namespace Executorlibs.Bilibili.Protocol.Clients
{
    public abstract class DanmakuClientBase : IDanmakuClient
    {
        protected abstract byte Version { get; }

        protected static readonly Memory<byte> heartBeatPacket = new byte[16] { 0, 0, 0, 16, 0, 16, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1 };

        protected static byte[] CreatePayload(int action)
        {
            byte[] buffer = new byte[16];
#if NET5_0_OR_GREATER
            ref BilibiliDanmakuProtocol protocol = ref Unsafe.As<byte, BilibiliDanmakuProtocol>(ref MemoryMarshal.GetArrayDataReference(buffer));
#else
            ref BilibiliDanmakuProtocol protocol = ref Unsafe.As<byte, BilibiliDanmakuProtocol>(ref MemoryMarshal.GetReference(buffer.AsSpan()));
#endif
            protocol.PacketLength = buffer.Length;
            protocol.Action = action;
            protocol.Magic = 16;
            protocol.Parameter = 1;
            protocol.Version = 2;
            protocol.ChangeEndian();
            return buffer;
        }

        protected static byte[] CreatePayload(int action, string body)
        {
            byte[] buffer = new byte[16 + Encoding.UTF8.GetByteCount(body)];
            Span<byte> span = buffer;
            ref BilibiliDanmakuProtocol protocol = ref Unsafe.As<byte, BilibiliDanmakuProtocol>(ref MemoryMarshal.GetReference(span));
            protocol.PacketLength = buffer.Length;
            protocol.Action = action;
            protocol.Magic = 16;
            protocol.Parameter = 1;
            protocol.Version = 2;
            protocol.ChangeEndian();
            Encoding.UTF8.GetBytes(body, span[16..]);
            return buffer;
        }

        protected static byte[] CreatePayload(int action, byte[] body)
        {
            byte[] buffer = new byte[16 + body.Length];
            Span<byte> span = buffer;
            ref BilibiliDanmakuProtocol protocol = ref Unsafe.As<byte, BilibiliDanmakuProtocol>(ref MemoryMarshal.GetReference(span));
            protocol.PacketLength = buffer.Length;
            protocol.Action = action;
            protocol.Magic = 16;
            protocol.Parameter = 1;
            protocol.Version = 2;
            protocol.ChangeEndian();
#if NET5_0_OR_GREATER
            Unsafe.CopyBlock(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), 16), ref MemoryMarshal.GetArrayDataReference(body), (uint)body.Length);
#else
            Unsafe.CopyBlock(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), 16), ref MemoryMarshal.GetReference(body.AsSpan()), (uint)body.Length);
#endif
            return buffer;
        }

        protected volatile bool _Connected;

        public virtual bool Connected => _Connected;

        public int RoomId => _Options.RoomId;

        protected CancellationTokenSource? _Cts = new();

        protected CancellationTokenSource? _WorkerCts;

        protected IBilibiliMessageHandlerInvoker _Invoker;

        protected IDanmakuServerProvider _CredentialProvider;

        protected DanmakuClientOptions _Options;

        protected DanmakuClientBase(IBilibiliMessageHandlerInvoker invoker, IOptionsSnapshot<DanmakuClientOptions> options, IDanmakuServerProvider credentialProvider)
        {
            _Invoker = invoker;
            _Options = options.Value;
            _CredentialProvider = credentialProvider;
        }

        private void CheckDisposed()
        {
            if (Volatile.Read(ref _Cts) == null)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }
        }

        public async Task ConnectAsync(CancellationToken token = default)
        {
            CheckDisposed();
            CancellationTokenSource? cts = Volatile.Read(ref _Cts);
            if (cts == null)
            {
                throw new ObjectDisposedException(this.GetType().Name);
            }
            CancellationToken ctsToken = cts.Token;
            CancellationToken createdToken;
            CancellationTokenSource? previousWCts = Volatile.Read(ref _WorkerCts);
            CancellationTokenSource? createdWCts = null;
            CancellationTokenSource createWCts()
            {
                createdWCts = CancellationTokenSource.CreateLinkedTokenSource(ctsToken, token);
                createdToken = createdWCts.Token;
                return createdWCts;
            }
            if (previousWCts != null || Interlocked.CompareExchange(ref _WorkerCts, createWCts(), null) != null)
            {
                createdWCts?.Dispose();
                throw new InvalidOperationException();
            }
            try
            {
                await InternalConnectAsync(createdToken);
#if NET5_0_OR_GREATER
                TaskCompletionSource tcs = new TaskCompletionSource();
#else
                TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
#endif
                ReceiveMessageAsyncLoop(tcs, createdWCts!, createdToken);
                await tcs.Task;
                _Connected = true;
                SendHeartBeatAsyncLoop(createdWCts!, createdToken);
            }
            catch
            {
                createdWCts!.Cancel();
                createdWCts.Dispose();
                Interlocked.CompareExchange(ref _WorkerCts, null!, createdWCts);
                throw;
            }
        }

        protected abstract Task InternalConnectAsync(CancellationToken token);

        public void Disconnect()
        {
            CancellationTokenSource? workerCts = Volatile.Read(ref _WorkerCts);
            if (workerCts != null)
            {
                Disconnect(workerCts);
            }
        }

        public void Disconnect(CancellationTokenSource workerCts, Exception? e = null)
        {
            if (Interlocked.CompareExchange(ref _WorkerCts, null, workerCts) == workerCts)
            {
                _Connected = false;
                workerCts.Cancel();
                workerCts.Dispose();
                InternalDisconnect();
                CancellationTokenSource? cts = Volatile.Read(ref _Cts);
                CancellationToken token;
                try
                {
                    token = cts == null ? new CancellationToken(true) : cts.Token;
                }
                catch (ObjectDisposedException)
                {
                    token = new CancellationToken(true);
                }
                _Invoker.HandleMessageAsync<IDisconnectedMessage>(this, new DisconnectedMessage { Exception = e, Time = DateTime.Now, Token = token });
            }
        }

        protected abstract void InternalDisconnect();

        protected abstract void InternalDispose(bool disposing);

        protected virtual void Dispose(bool disposing)
        {
            CancellationTokenSource? previousCts = Volatile.Read(ref _Cts);
            if (previousCts != null && Interlocked.CompareExchange(ref _Cts, null, previousCts) == previousCts)
            {
                try { Disconnect(); } catch { }
                previousCts.Cancel();
                previousCts.Dispose();
                InternalDispose(disposing);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract ValueTask SendAsync(Memory<byte> memory, CancellationToken token);

        protected abstract ValueTask ReceiveAsync(Memory<byte> memory, CancellationToken token);

        private async void SendHeartBeatAsyncLoop(CancellationTokenSource workerCts, CancellationToken token)
        {
            double tickFrequency = 10000 * 1000 / (double)Stopwatch.Frequency;
            long ticks;
            while (true)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    ticks = Stopwatch.GetTimestamp();
                    await SendAsync(heartBeatPacket, token).ConfigureAwait(false);
                    TimeSpan toSleep = _Options.HeartbeatInterval - TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - ticks) * tickFrequency));
                    if (toSleep > default(TimeSpan))
                    {
                        await Task.Delay(toSleep, token);
                    }
                    else
                    {
                        throw new TimeoutException("Heartbeat timed out.");
                    }
                }
                catch (Exception e)
                {
                    Disconnect(workerCts, e);
                    return;
                }
            }
        }

#if NET5_0_OR_GREATER
        private async void ReceiveMessageAsyncLoop(TaskCompletionSource tcs, CancellationTokenSource workerCts, CancellationToken token)
#else
        private async void ReceiveMessageAsyncLoop(TaskCompletionSource<int> tcs, CancellationTokenSource workerCts, CancellationToken token)
#endif
        {
            ReceiveMethodLocals locals = default;
            locals.protocolBuffer = new byte[16];
            locals.payload = new byte[4096];
            locals.decompressBuffer = Array.Empty<byte>();
            while (true)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    await ReceiveAsync(locals.protocolBuffer, token).ConfigureAwait(false);
                    locals.Protocol.ChangeEndian();
                    if (locals.Protocol.PacketLength > 0)
                    {
                        if (locals.payloadLength > 65535)
                        {
                            throw new InvalidDataException($"包长度过大:{ locals.payloadLength}");
                        }
                        if (locals.payloadLength > locals.payload.Length)
                        {
                            locals.payload = new byte[locals.payloadLength];
                        }
                        await ReceiveAsync(new Memory<byte>(locals.payload, 0, locals.payloadLength), token);
                        if (locals.Protocol.Action == 8)
                        {
#if NET5_0_OR_GREATER
                            tcs.TrySetResult();
#else
                            tcs.TrySetResult(0);
#endif
                            try
                            {
                                _ = _Invoker.HandleMessageAsync<IConnectedMessage>(this, new ConnectedMessage { Time = DateTime.Now });
                            }
                            catch // 失败了关我啥事儿
                            {

                            }
                        }
                        else
                        {
                            HandlePayload(ref locals);
                        }
                    }
                }
                catch (OperationCanceledException e)
                {
                    bool result = tcs.TrySetCanceled(token);
                    Disconnect(workerCts, result ? null : e);
                    return;
                }
                catch (Exception e)
                {
                    bool result = tcs.TrySetException(e);
                    Disconnect(workerCts, result ? null : e);
                    return;
                }
            }
        }

        protected virtual void HandlePayload(ref ReceiveMethodLocals locals)
        {
            ProcessDanmaku(in locals.Protocol, locals.payload);
        }

        protected void ProcessDanmaku(in BilibiliDanmakuProtocol protocol, byte[] buffer)
        {
            switch (protocol.Action)
            {
                case 3:
                    {
                        try
                        {
#if NET5_0_OR_GREATER
                            uint popularity = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetArrayDataReference(buffer));
#else
                            uint popularity = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(buffer.AsSpan()));
#endif
#if !BIGENDIAN
                            popularity = BinaryPrimitives.ReverseEndianness(popularity);
#endif
                            _Invoker.HandleMessageAsync<IPopularityMessage>(this, new PopularityMessage { Popularity = popularity, Time = DateTime.Now });
                        }
                        catch
                        {
                            // ToDoooooooo
                        }
                        break;
                    }
                case 5:
                    {
                        try
                        {
                            JsonElement root = JsonSerializer.Deserialize<JsonElement>(new ReadOnlySpan<byte>(buffer, 0, protocol.PacketLength - 16));
                            _Invoker.HandleRawdataAsync(this, root);
                        }
                        catch
                        {
                            // ToDoooooooo
                        }
                        break;
                    }
            }
        }

        protected byte[] CreateJoinRoomPayload(int roomId, int userId, string token)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                uid = userId,
                roomid = roomId,
                protover = Version,
                platform = "web",
                clientver = "1.13.4",
                type = 2,
                key = token
            });
            return CreatePayload(7, json);
        }

        protected static ref BilibiliDanmakuProtocol Interpret(byte[] protocolBuffer)
        {
#if NET5_0_OR_GREATER
            return ref Unsafe.As<byte, BilibiliDanmakuProtocol>(ref MemoryMarshal.GetArrayDataReference(protocolBuffer));
#else
            return ref Interpret(protocolBuffer.AsSpan());
#endif
        }

        protected static ref BilibiliDanmakuProtocol Interpret(ReadOnlySpan<byte> protocolSpan)
        {
            return ref Unsafe.As<byte, BilibiliDanmakuProtocol>(ref MemoryMarshal.GetReference(protocolSpan));
        }

        protected struct ReceiveMethodLocals
        {
            public byte[] protocolBuffer;

            public byte[] payload;

            public byte[] decompressBuffer;

            public int payloadLength => Protocol.PacketLength - 16;

            public ref BilibiliDanmakuProtocol Protocol => ref Interpret(protocolBuffer);
        }
    }
}