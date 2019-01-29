﻿using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace NetCoreServer
{
    /// <summary>
    /// SSL client is used to read/write data from/into the connected SSL server
    /// </summary>
    /// <remarks>Thread-safe</remarks>
    public class SslClient : IDisposable
    {
        /// <summary>
        /// Initialize SSL client with a given server IP address and port number
        /// </summary>
        /// <param name="context">SSL context</param>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        public SslClient(SslContext context, IPAddress address, int port) : this(context, new IPEndPoint(address, port)) {}
        /// <summary>
        /// Initialize SSL client with a given server IP address and port number
        /// </summary>
        /// <param name="context">SSL context</param>
        /// <param name="address">IP address</param>
        /// <param name="port">Port number</param>
        public SslClient(SslContext context, string address, int port) : this(context, new IPEndPoint(IPAddress.Parse(address), port)) { Address = address; }
        /// <summary>
        /// Initialize SSL client with a given IP endpoint
        /// </summary>
        /// <param name="context">SSL context</param>
        /// <param name="endpoint">IP endpoint</param>
        public SslClient(SslContext context, IPEndPoint endpoint)
        {
            Id = Guid.NewGuid();
            Address = endpoint.Address.ToString();
            Context = context;
            Endpoint = endpoint;
        }

        /// <summary>
        /// SSL client Id
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// SSL server DNS address
        /// </summary>
        public string Address { get; set; }
        /// <summary>
        /// SSL context
        /// </summary>
        public SslContext Context { get; private set; }
        /// <summary>
        /// IP endpoint
        /// </summary>
        public IPEndPoint Endpoint { get; private set; }
        /// <summary>
        /// Socket
        /// </summary>
        public Socket Socket { get; private set; }

        /// <summary>
        /// Number of bytes pending sent by the client
        /// </summary>
        public long BytesPending { get; private set; }
        /// <summary>
        /// Number of bytes sending by the client
        /// </summary>
        public long BytesSending { get; private set; }
        /// <summary>
        /// Number of bytes sent by the client
        /// </summary>
        public long BytesSent { get; private set; }
        /// <summary>
        /// Number of bytes received by the client
        /// </summary>
        public long BytesReceived { get; private set; }

        /// <summary>
        /// Option: keep alive
        /// </summary>
        /// <remarks>
        /// This option will setup SO_KEEPALIVE if the OS support this feature
        /// </remarks>
        public bool OptionKeepAlive { get; set; }
        /// <summary>
        /// Option: no delay
        /// </summary>
        /// <remarks>
        /// This option will enable/disable Nagle's algorithm for SSL protocol
        /// </remarks>
        public bool OptionNoDelay { get; set; }
        /// <summary>
        /// Option: receive buffer size
        /// </summary>
        public int OptionReceiveBufferSize
        {
            get => Socket.ReceiveBufferSize;
            set => Socket.ReceiveBufferSize = value;
        }
        /// <summary>
        /// Option: send buffer size
        /// </summary>
        public int OptionSendBufferSize
        {
            get => Socket.SendBufferSize;
            set => Socket.SendBufferSize = value;
        }

        #region Connect/Disconnect client

        private bool _connecting;
        private bool _handshaking;
        private SocketAsyncEventArgs _connectEventArg;
        private SslBuffer _sslBuffer;
        private SslStream _sslStream;

        /// <summary>
        /// Is the client connected?
        /// </summary>
        public bool IsConnected { get; private set; }
        /// <summary>
        /// Is the client handshaked?
        /// </summary>
        public bool IsHandshaked { get; private set; }

        /// <summary>
        /// Connect the client
        /// </summary>
        /// <returns>'true' if the client was successfully connected, 'false' if the client failed to connect</returns>
        public virtual bool Connect()
        {
            if (IsConnected || IsHandshaked || _connecting || _handshaking)
                return false;

            // Setup buffers
            _receiveBuffer = new Buffer();
            _sendBufferMain = new Buffer();
            _sendBufferFlush = new Buffer();

            // Setup event args
            _connectEventArg = new SocketAsyncEventArgs();
            _connectEventArg.RemoteEndPoint = Endpoint;
            _connectEventArg.Completed += OnAsyncCompleted;
            _receiveEventArg = new SocketAsyncEventArgs();
            _receiveEventArg.Completed += OnAsyncCompleted;
            _sendEventArg = new SocketAsyncEventArgs();
            _sendEventArg.Completed += OnAsyncCompleted;

            // Create a new client socket
            Socket = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // Async connect to the server
            _connecting = true;
            if (!Socket.ConnectAsync(_connectEventArg))
                ProcessConnect(_connectEventArg);

            return true;
        }

        /// <summary>
        /// Disconnect the client
        /// </summary>
        /// <returns>'true' if the client was successfully disconnected, 'false' if the client is already disconnected</returns>
        public virtual bool Disconnect()
        {
            if (!IsConnected && !_connecting)
                return false;

            // Cancel connecting operation
            if (_connecting)
                Socket.CancelConnectAsync(_connectEventArg);

            // Reset event args
            _connectEventArg.Completed -= OnAsyncCompleted;
            _receiveEventArg.Completed -= OnAsyncCompleted;
            _sendEventArg.Completed -= OnAsyncCompleted;

            try
            {
                // Switch to the network SSL stream
                _sslBuffer.IsNetworkStream = true;

                // Dispose the SSL stream & buffer
                _sslStream.Dispose();
                _sslBuffer.Dispose();

                // Shutdown the socket associated with the client
                Socket.Shutdown(SocketShutdown.Both);

                // Close the client socket
                Socket.Close();

                // Dispose the client socket
                Socket.Dispose();
            }
            catch (ObjectDisposedException) {}

            // Update the handshaked flag
            IsHandshaked = false;

            // Update the connected flag
            IsConnected = false;

            // Update sending/receiving flags
            _receiving = false;
            _sending = false;

            // Clear send/receive buffers
            ClearBuffers();

            // Call the client disconnected handler
            OnDisconnected();

            return true;
        }

        /// <summary>
        /// Reconnect the client
        /// </summary>
        /// <returns>'true' if the client was successfully reconnected, 'false' if the client is already reconnected</returns>
        public virtual bool Reconnect()
        {
            if (!Disconnect())
                return false;

            while (IsConnected)
                Thread.Yield();

            return Connect();
        }

        #endregion

        #region Send/Recieve data

        // Receive buffer
        private bool _receiving;
        private Buffer _receiveBuffer;
        private SocketAsyncEventArgs _receiveEventArg;
        // Send buffer
        private readonly object _sendLock = new object();
        private bool _sending;
        private Buffer _sendBufferMain;
        private Buffer _sendBufferFlush;
        private SocketAsyncEventArgs _sendEventArg;
        private long _sendBufferFlushOffset;

        /// <summary>
        /// Send data to the server
        /// </summary>
        /// <param name="buffer">Buffer to send</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the client is not connected</returns>
        public virtual bool Send(byte[] buffer) { return Send(buffer, 0, buffer.Length); }

        /// <summary>
        /// Send data to the server
        /// </summary>
        /// <param name="buffer">Buffer to send</param>
        /// <param name="offset">Buffer offset</param>
        /// <param name="size">Buffer size</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the client is not connected</returns>
        public virtual bool Send(byte[] buffer, long offset, long size)
        {
            if (!IsHandshaked)
                return false;

            if (size == 0)
                return true;

            lock (_sendLock)
            {
                // Detect multiple send handlers
                bool sendRequired = _sendBufferMain.IsEmpty || _sendBufferFlush.IsEmpty;

                // Fill the main send buffer
                _sendBufferMain.Append(buffer, offset, size);

                // Update statistic
                BytesPending = _sendBufferMain.Size;

                // Avoid multiple send handlers
                if (!sendRequired)
                    return true;
            }

            // Try to send the main buffer
            TrySend();

            return true;
        }

        /// <summary>
        /// Send text to the server
        /// </summary>
        /// <param name="text">Text string to send</param>
        /// <returns>'true' if the text was successfully sent, 'false' if the client is not connected</returns>
        public virtual bool Send(string text) { return Send(Encoding.UTF8.GetBytes(text)); }

        /// <summary>
        /// Try to receive new data
        /// </summary>
        private void TryReceive()
        {
            if (_receiving)
                return;

            if (!IsHandshaked)
                return;

            try
            {
                // Async receive with the receive handler
                _receiving = true;
                _receiveEventArg.SetBuffer(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity);
                if (!Socket.ReceiveAsync(_receiveEventArg))
                    ProcessReceive(_receiveEventArg);
            }
            catch (ObjectDisposedException) {}
        }

        /// <summary>
        /// Try to send pending data
        /// </summary>
        private void TrySend()
        {
            if (_sending)
                return;

            if (!IsHandshaked)
                return;

            // Swap send buffers
            if (_sendBufferFlush.IsEmpty)
            {
                lock (_sendLock)
                {
                    // Swap flush and main buffers
                    _sendBufferFlush = Interlocked.Exchange(ref _sendBufferMain, _sendBufferFlush);
                    _sendBufferFlushOffset = 0;

                    // Update statistic
                    BytesPending = 0;
                    BytesSending += _sendBufferFlush.Size;

                    // Write SSL stream
                    _sslStream.Write(_sendBufferFlush.Data, 0, (int)_sendBufferFlush.Size);
                    _sslStream.Flush();
                }
            }
            else
                return;

            // Check if the flush buffer is empty
            if (_sendBufferFlush.IsEmpty)
            {
                // Call the empty send buffer handler
                OnEmpty();
                return;
            }

            try
            {
                // Async write with the write handler
                _sending = true;
                _sendEventArg.SetBuffer(_sslBuffer.SendBuffer.Data, (int)_sendBufferFlushOffset, (int)(_sslBuffer.SendBuffer.Size - _sendBufferFlushOffset));
                if (!Socket.SendAsync(_sendEventArg))
                    ProcessSend(_sendEventArg);
            }
            catch (ObjectDisposedException) {}
        }

        /// <summary>
        /// Clear send/receive buffers
        /// </summary>
        private void ClearBuffers()
        {
            lock (_sendLock)
            {
                // Clear SSL buffer
                _sslBuffer.SendBuffer.Clear();

                // Clear send buffers
                _sendBufferMain.Clear();
                _sendBufferFlush.Clear();
                _sendBufferFlushOffset= 0;

                // Update statistic
                BytesPending = 0;
                BytesSending = 0;
            }
        }

        #endregion

        #region IO processing

        private readonly byte[] _receiveChunk = new byte[4096];

        /// <summary>
        /// This method is called whenever a receive or send operation is completed on a socket
        /// </summary>
        private void OnAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            // Determine which type of operation just completed and call the associated handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    ProcessConnect(e);
                    break;
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }

        }

        /// <summary>
        /// This method is invoked when an asynchronous connect operation completes
        /// </summary>
        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            _connecting = false;

            if (e.SocketError == SocketError.Success)
            {
                // Apply the option: keep alive
                if (OptionKeepAlive)
                    Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // Apply the option: no delay
                if (OptionNoDelay)
                    Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);

                // Prepare receive & send buffers
                _receiveBuffer.Reserve(OptionReceiveBufferSize);
                _sendBufferMain.Reserve(OptionSendBufferSize);
                _sendBufferFlush.Reserve(OptionSendBufferSize);

                // Reset statistic
                BytesPending = 0;
                BytesSending = 0;
                BytesSent = 0;
                BytesReceived = 0;

                // Update the connected flag
                IsConnected = true;

                // Call the client connected handler
                OnConnected();

                // Create SSL inner stream buffer
                _sslBuffer = new SslBuffer(new NetworkStream(Socket, false), Socket.ReceiveBufferSize, Socket.SendBufferSize);

                // Create SSL stream
                _sslStream = (Context.CertificateValidationCallback != null) ? new SslStream(_sslBuffer, false, Context.CertificateValidationCallback) : new SslStream(_sslBuffer, false);

                // Begin the SSL handshake
                _sslStream.BeginAuthenticateAsClient(Address, (Context.Certificates != null) ? Context.Certificates : new X509CertificateCollection(new[] { Context.Certificate }), Context.Protocols, true, ProcessHandshake, this);
            }
            else
            {
                // Call the client disconnected handler
                SendError(e.SocketError);
                OnDisconnected();
            }
        }

        /// <summary>
        /// This method is invoked when an asynchronous handshake operation completes
        /// </summary>
        private void ProcessHandshake(IAsyncResult result)
        {
            try
            {
                // End the SSL handshake
                _sslStream.EndAuthenticateAsClient(result);

                // Switch to the inner SSL buffer
                _sslBuffer.IsNetworkStream = false;

                if (IsHandshaked)
                    return;

                // Update the handshaked flag
                IsHandshaked = true;

                // Call the session handshaked handler
                OnHandshaked();

                // Call the empty send buffer handler
                if (_sendBufferMain.IsEmpty)
                    OnEmpty();

                // Try to receive something from the server
                TryReceive();
            }
            catch (Exception)
            {
                SendError(SocketError.NotConnected);
                Disconnect();
            }
        }

        /// <summary>
        /// This method is invoked when an asynchronous receive operation completes
        /// </summary>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            _receiving = false;

            if (!IsHandshaked)
                return;

            long size = e.BytesTransferred;

            // Received some data from the client
            if (size > 0)
            {
                // Update statistic
                BytesReceived += size;

                // If the receive buffer is full increase its size
                if (_receiveBuffer.Capacity == size)
                    _receiveBuffer.Reserve(2 * size);

                // Append SSL receive buffer
                _sslBuffer.ReceiveBuffer.Append(_receiveBuffer.Data, 0, size);

                // Read SSL stream as data chunks...
                do
                {
                    // Read the next chunk from the SSL stream
                    int length = _sslStream.Read(_receiveChunk, 0, _receiveChunk.Length);
                    if (length <= 0)
                    {
                        // Special check for SSL server shutdown message...
                        if (_sslBuffer.ReceiveBuffer.Size >= 31)
                        {
                            if ((_sslBuffer.ReceiveBuffer.Data[0] == 0x15) && 
                                (_sslBuffer.ReceiveBuffer.Data[1] == 0x03) && 
                                (_sslBuffer.ReceiveBuffer.Data[2] == 0x03) && 
                                (_sslBuffer.ReceiveBuffer.Data[3] == 0x00) && 
                                (_sslBuffer.ReceiveBuffer.Data[4] == 0x1A))
                            size = 0;
                        }
                        break;
                    }

                    // Call the buffer received handler
                    OnReceived(_receiveChunk, length);
                } while (true);

                // Clear SSL receive buffer
                _sslBuffer.ReceiveBuffer.Clear();
            }

            // Try to receive again if the client is valid
            if (e.SocketError == SocketError.Success)
            {
                // If zero is returned from a read operation, the remote end has closed the connection
                if (size > 0)
                    TryReceive();
                else
                    Disconnect();
            }
            else
            {
                SendError(e.SocketError);
                Disconnect();
            }
        }

        /// <summary>
        /// This method is invoked when an asynchronous send operation completes
        /// </summary>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            _sending = false;

            if (!IsHandshaked)
                return;

            long size = e.BytesTransferred;

            // Send some data to the client
            if (size > 0)
            {
                // Update statistic
                BytesSending -= size;
                BytesSent += size;

                // Increase the flush buffer offset
                _sendBufferFlushOffset += size;

                // Successfully send the whole flush buffer
                if (_sendBufferFlushOffset == _sslBuffer.SendBuffer.Size)
                {
                    // Clear the flush buffer
                    _sslBuffer.SendBuffer.Clear();
                    _sendBufferFlush.Clear();
                    _sendBufferFlushOffset = 0;
                }

                // Call the buffer sent handler
                OnSent(size, BytesPending + BytesSending);
            }

            // Try to send again if the client is valid
            if (e.SocketError == SocketError.Success)
                TrySend();
            else
            {
                SendError(e.SocketError);
                Disconnect();
            }
        }

        #endregion

        #region Session handlers

        /// <summary>
        /// Handle client connected notification
        /// </summary>
        protected virtual void OnConnected() {}
        /// <summary>
        /// Handle client handshaked notification
        /// </summary>
        protected virtual void OnHandshaked() {}
        /// <summary>
        /// Handle client disconnected notification
        /// </summary>
        protected virtual void OnDisconnected() {}

        /// <summary>
        /// Handle buffer received notification
        /// </summary>
        /// <param name="buffer">Received buffer</param>
        /// <param name="size">Received buffer size</param>
        /// <remarks>
        /// Notification is called when another chunk of buffer was received from the server
        /// </remarks>
        protected virtual void OnReceived(byte[] buffer, long size) {}
        /// <summary>
        /// Handle buffer sent notification
        /// </summary>
        /// <param name="sent">Size of sent buffer</param>
        /// <param name="pending">Size of pending buffer</param>
        /// <remarks>
        /// Notification is called when another chunk of buffer was sent to the server.
        /// This handler could be used to send another buffer to the server for instance when the pending size is zero.
        /// </remarks>
        protected virtual void OnSent(long sent, long pending) {}

        /// <summary>
        /// Handle empty send buffer notification
        /// </summary>
        /// <remarks>
        /// Notification is called when the send buffer is empty and ready for a new data to send.
        /// This handler could be used to send another buffer to the server.
        /// </remarks>
        protected virtual void OnEmpty() {}

        /// <summary>
        /// Handle error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        protected virtual void OnError(SocketError error) {}

        #endregion

        #region Error handling

        /// <summary>
        /// Send error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        private void SendError(SocketError error)
        {
            // Skip disconnect errors
            if ((error == SocketError.ConnectionAborted) ||
                (error == SocketError.ConnectionRefused) ||
                (error == SocketError.ConnectionReset) ||
                (error == SocketError.OperationAborted))
                return;

            OnError(error);
        }

        #endregion

        #region IDisposable implementation

        // Disposed flag.
        private bool _disposed;

        // Implement IDisposable.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposingManagedResources)
        {
            // The idea here is that Dispose(Boolean) knows whether it is
            // being called to do explicit cleanup (the Boolean is true)
            // versus being called due to a garbage collection (the Boolean
            // is false). This distinction is useful because, when being
            // disposed explicitly, the Dispose(Boolean) method can safely
            // execute code using reference type fields that refer to other
            // objects knowing for sure that these other objects have not been
            // finalized or disposed of yet. When the Boolean is false,
            // the Dispose(Boolean) method should not execute code that
            // refer to reference type fields because those objects may
            // have already been finalized."

            if (!_disposed)
            {
                if (disposingManagedResources)
                {
                    // Dispose managed resources here...
                    Disconnect();
                }

                // Dispose unmanaged resources here...

                // Set large fields to null here...

                // Mark as disposed.
                _disposed = true;
            }
        }

        // Use C# destructor syntax for finalization code.
        ~SslClient()
        {
            // Simply call Dispose(false).
            Dispose(false);
        }

        #endregion
    }
}