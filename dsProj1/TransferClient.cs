using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.IO;


namespace dsProj1
{
    public delegate void TransferEventHandler(object sender, TransferQueue queue);
    public delegate void ConnectCallback(object sender, string error);

    public class TransferClient
    {
        //To hold connection Socket.
        private Socket _baseSocket;

        //Receive buffer.
        private byte[] _buffer = new byte[8192];

        //To connect.
        private ConnectCallback _connectCallback;

        //This stores our all transfers(Download, Upload)
        private Dictionary<int, TransferQueue> _transfers = new Dictionary<int, TransferQueue>();

        public Dictionary<int, TransferQueue> Transfers
        {
            get { return _transfers; }
        }

        public bool Closed
        {
            get;
            private set;
        }

        //The folder we will save the files too. 
        public string OutputFolder
        {
            get;
            set;
        }

        //IP and port of connected socket.
        public IPEndPoint EndPoint
        {
            get;
            private set;
        }

        public event TransferEventHandler Queued; 
        public event TransferEventHandler ProgressChanged; 
        public event TransferEventHandler Stopped;
        public event TransferEventHandler Complete; 
        public event EventHandler Disconnected;

        //Constructor for the client when we want to connect.
        public TransferClient()
        {
            _baseSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }

        //Constructor we will use once a connection is accepted by the listener.
        public TransferClient(Socket sock)
        {
            //Set the socket.
            _baseSocket = sock;
            //Grab the end point.
            EndPoint = (IPEndPoint)_baseSocket.RemoteEndPoint;
        }

        public void Connect(string hostName, int port, ConnectCallback callback)
        {
            
            _connectCallback = callback;
            
            _baseSocket.BeginConnect(hostName, port, connectCallback, null);
        }

        private void connectCallback(IAsyncResult ar)
        {
            string error = null;
            try //.NET will throw an exception if a connection could not be made.
            {
                
                _baseSocket.EndConnect(ar);
                EndPoint = (IPEndPoint)_baseSocket.RemoteEndPoint;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            //After everything is done, call the callback.
            _connectCallback(this, error);
        }

        public void Run()
        {
            try
            {
                //Begin receiving the information.
                //.NET can throw an exception here as well if the socket disconnects.

                _baseSocket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.Peek, receiveCallback, null);
            }
            catch
            {
                //If an exception is thrown, close the client.
                Close();
            }
        }

        public void QueueTransfer(string fileName)
        {
            try
            {
                //We will create our upload queue.
                TransferQueue queue = TransferQueue.CreateUploadQueue(this, fileName);

                //Add the transfer to our transfer list.
                _transfers.Add(queue.ID, queue);

                //Now we will create and build our queue packet.
                PacketWriter pw = new PacketWriter();
                pw.Write((byte)Headers.Queue);
                pw.Write(queue.ID);
                pw.Write(queue.Filename);
                pw.Write(queue.Length);
                Send(pw.GetBytes());

                //Call queued
                if (Queued != null)
                {
                    Queued(this, queue);
                }
            }
            catch
            {
            }
        }

        public void StartTransfer(TransferQueue queue)
        {
            //We'll create our start packet.
            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Start);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
        }

        public void StopTransfer(TransferQueue queue)
        {
            //If we're the uploading transfer, we'll just stop it.
            if (queue.Type == QueueType.Upload)
            {
                queue.Stop();
            }

            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Stop);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
            //Don't forget to close the queue.
            queue.Close();
        }

        public void PauseTransfer(TransferQueue queue)
        {
            //Pause the queue.
            //This doesn't have to be done for the downloading queue, but its here for a reason.
            queue.Pause();

            PacketWriter pw = new PacketWriter();
            pw.Write((byte)Headers.Pause);
            pw.Write(queue.ID);
            Send(pw.GetBytes());
        }

        public int GetOverallProgress()
        {
            int overall = 0;
            try
            {
                foreach (KeyValuePair<int, TransferQueue> pair in _transfers)
                {
                    //Add the progress of each transfer to our variable for calculation
                    overall += pair.Value.Progress;
                }

                if (overall > 0)
                {
                    //We'll use the formula of
                    //(OVERALL_PROGRESS * 100) / (PROGRESS_COUNT * 100)
                    //To gather the overall progess of every transfer.
                    overall = (overall * 100) / (_transfers.Count * 100);
                }
            }
            catch { overall = 0; /*If there was an issue, just return 0*/ }

            return overall;
        }

        public void Send(byte[] data)
        {
            //If our client is disposed, just return.
            if (Closed)
                return;

            //Use a lock of this instance so we can't send multiple things at a time.
            lock (this)
            {
                try
                {
                    //Send the size of the packet.
                    _baseSocket.Send(BitConverter.GetBytes(data.Length), 0, 4, SocketFlags.None);
                    //And then the actual packet.
                    _baseSocket.Send(data, 0, data.Length, SocketFlags.None);
                }
                catch
                {
                    Close();
                }
            }
        }

        public void Close()
        {
            //INSERTED - NOT IN TUTORIAL
            if (Closed)
                return;
            //
            Closed = true;
            _baseSocket.Close(); //Close the socket
            _transfers.Clear(); //Clear the transfers
            _transfers = null;
            _buffer = null;
            OutputFolder = null;

            //Call disconnected
            if (Disconnected != null)
                Disconnected(this, EventArgs.Empty);
        }

        private void process()
        {
            PacketReader pr = new PacketReader(_buffer); //Create our packet reader.

            Headers header = (Headers)pr.ReadByte(); //Read and cast our header.

            switch (header)
            {
                case Headers.Queue:
                    {
                        //Read the ID, Filename and length of the file (For progress) from the packet.
                        int id = pr.ReadInt32();
                        string fileName = pr.ReadString();
                        long length = pr.ReadInt64();

                        //Create our download queue.
                        TransferQueue queue = TransferQueue.CreateDownloadQueue(this, id, Path.Combine(OutputFolder,
                            Path.GetFileName(fileName)), length);

                        //Add it to our transfer list.
                        _transfers.Add(id, queue);

                        //Call queued.
                        if (Queued != null)
                        {
                            Queued(this, queue);
                        }
                    }
                    break;
                case Headers.Start:
                    {
                        //Read the ID
                        int id = pr.ReadInt32();

                        //Start the upload.
                        if (_transfers.ContainsKey(id))
                        {
                            _transfers[id].Start();
                        }
                    }
                    break;
                case Headers.Stop:
                    {
                        //Read the ID
                        int id = pr.ReadInt32();

                        if (_transfers.ContainsKey(id))
                        {
                            //Get the queue.
                            TransferQueue queue = _transfers[id];

                            //Stop and close the queue
                            queue.Stop();
                            queue.Close();

                            //Call the stopped event.
                            if (Stopped != null)
                                Stopped(this, queue);

                            //Remove the queue
                            _transfers.Remove(id);
                        }
                    }
                    break;
                case Headers.Pause:
                    {
                        int id = pr.ReadInt32();

                        //Pause the upload.
                        if (_transfers.ContainsKey(id))
                        {
                            _transfers[id].Pause();
                        }
                    }
                    break;
                case Headers.Chunk:
                    {
                        //Read the ID, index, size and buffer from the packet.
                        int id = pr.ReadInt32();
                        long index = pr.ReadInt64();
                        int size = pr.ReadInt32();
                        byte[] buffer = pr.ReadBytes(size);

                        //Get the queue.
                        TransferQueue queue = _transfers[id];

                        //Write the newly transferred bytes to the queue based on the write index.
                        queue.Write(buffer, index);

                        //Get the progress of the current transfer with the formula
                        //(AMOUNT_TRANSFERRED * 100) / COMPLETE SIZE
                        queue.Progress = (int)((queue.Transferred * 100) / queue.Length);

                        //This will prevent the us from calling progress changed multiple times.
                        /* Such as
                         * 2, 2, 2, 2, 2, 2 (Since the actual progress minus the decimals will be the same for a bit
                         * It will be
                         * 1, 2, 3, 4, 5, 6
                         * Instead*/
                        if (queue.LastProgress < queue.Progress)
                        {
                            queue.LastProgress = queue.Progress;

                            if (ProgressChanged != null)
                            {
                                ProgressChanged(this, queue);
                            }

                            //If the transfer is complete, call the event.
                            if (queue.Progress == 100)
                            {
                                queue.Close();

                                if (Complete != null)
                                {
                                    Complete(this, queue);
                                }
                            }
                        }
                    }
                    break;
            }
            pr.Dispose(); //Dispose the reader.
        }

        private void receiveCallback(IAsyncResult ar)
        {
            try
            {
                //Call EndReceive to get the amount available.
                int found = _baseSocket.EndReceive(ar);

                //If found is or is greater than 4 (Meaning our size bytes are there)
                //We will actually read it from our buffer.
                //If its less than 4, Run will be called again.
                if (found >= 4)
                {
                    //We will receive our size bytes
                    _baseSocket.Receive(_buffer, 0, 4, SocketFlags.None);

                    //Get the int value.
                    int size = BitConverter.ToInt32(_buffer, 0);

                    //And attempt to read our
                    int read = _baseSocket.Receive(_buffer, 0, size, SocketFlags.None);

                    /*Data could still be fragmented, so we'll check our read size against the actual size.
                     * If read is less than size, we'll keep receiving until we have the full packet.
                     * It will only take a few milliseconds or a second (In most cases), so we can use a sync-
                     * receive*/
                    while (read < size)
                    {
                        read += _baseSocket.Receive(_buffer, read, size - read, SocketFlags.None);
                    }

                    //We'll call process to handle the data we received.
                    process();
                }

                Run();
            }
            catch
            {
                Close();
            }
        }

        internal void callProgressChanged(TransferQueue queue)
        {
            if (ProgressChanged != null)
            {
                ProgressChanged(this, queue);
            }
        }
    }
}
