﻿using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace DataSpreads.SignalW.Connections {

    public abstract class Channel {

        public abstract Task WriteAsync(MemoryStream item, CancellationToken cancellationToken = new CancellationToken());

        public abstract bool TryComplete();

        public abstract Task<MemoryStream> ReadAsync(CancellationToken cancellationToken = new CancellationToken());

        public abstract Task Completion { get; }
    }

    public class WsChannel : Channel {
        private static RecyclableMemoryStreamManager MemoryStreams = new RecyclableMemoryStreamManager();

        private readonly WebSocket _ws;
        private readonly Format _format;
        private TaskCompletionSource<bool> _tcs;
        private CancellationTokenSource _cts;
        
        public WsChannel(WebSocket ws, Format format) {
            _ws = ws;
            _format = format;
            _tcs = new TaskCompletionSource<bool>();
            _cts = new CancellationTokenSource();
        }

        public override async Task WriteAsync(MemoryStream item, CancellationToken cancellationToken = new CancellationToken()) {
            var rms = item as RecyclableMemoryStream;
            if (rms != null) {
                try {
                    foreach (var chunk in rms.Chunks)
                    {
                        var type = _format == Format.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                        await _ws.SendAsync(chunk, type, true, _cts.Token);
                    }
                } catch {
                    if (!_cts.IsCancellationRequested) return;
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure", CancellationToken.None);
                    _tcs.TrySetResult(true);
                }
                return;
            }
            throw new NotImplementedException();
        }

        public override bool TryComplete() {
            if (_cts.IsCancellationRequested) return false;
            _cts.Cancel();
            return true;
        }

        public override async Task<MemoryStream> ReadAsync(CancellationToken cancellationToken = new CancellationToken()) {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            var ms = MemoryStreams.GetStream();
            WebSocketReceiveResult result;
            try {

                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                while (!result.CloseStatus.HasValue && !_cts.IsCancellationRequested) {
                    ms.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    } else {
                        break;
                    }
                }
                if (result.CloseStatus.HasValue) {
                    _cts.Cancel();
                    await _ws.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
                    _tcs.TrySetResult(true);
                    ms.Dispose();
                    ms = null;
                } else if (_cts.IsCancellationRequested) {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure", CancellationToken.None);
                    _tcs.TrySetResult(true);
                    ms.Dispose();
                    ms = null;
                }
            } catch {
                if (_cts.IsCancellationRequested) {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "NormalClosure", CancellationToken.None);
                    _tcs.TrySetResult(true);
                }
                ms.Dispose();
                ms = null;
            } finally {
                ArrayPool<byte>.Shared.Return(buffer, true);
            }
            return ms;
        }

        public override Task Completion => _tcs.Task;
    }
}