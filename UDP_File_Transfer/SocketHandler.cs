using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;

namespace UDP_File_Transfer
{
    internal enum OpCode : ushort
    {
        Nan = 0,
        Eco,
        Aec,
        Ack,
        Sft,
        Trd,
        Eft,
        Fin
    }

    internal enum ConnectionStatus
    {
        InActive = 0,
        Connecting,
        Conntected,
        TransferData
    }

    internal struct FileData
    {
        public int SequenceNumber;
        public List<byte> Data;
    }

    public class SocketHandler
    {
        private ConnectionStatus _status;

        private ulong _sequenceNumber;
        private ulong _expectedSequenceNumber;

        private readonly Thread _receiveThread;
        private readonly Thread _sendThread;

        private readonly UdpClient _udpClient;

        private bool _receiveThreadRunning;
        private bool _sendThreadRunning;

        private readonly string _myAddress;
        private readonly int _myPort;

        private string _destAddress;
        private int _destPort;

        public SocketHandler(string anAdress, int aPort, ulong sequenceNumber)
        {
            _sequenceNumber = sequenceNumber;

            _myAddress = anAdress;
            _myPort = aPort;
            
            _receiveThreadRunning = false;
            _sendThreadRunning = false;

            _receiveThread = new Thread(ReceiveThreadFunction);
            _sendThread = new Thread(SendThreadFunction);

            _udpClient = new UdpClient(aPort);
        }

        private void ReceiveThreadFunction(object obj)
        {
            var receiveBuffer = new List<FileData>();

            var remoteIpEndPoint = new IPEndPoint(IPAddress.Any, _myPort);
            Console.WriteLine("ReceiveThreadFunction started");

            while (_receiveThreadRunning)
            {
                try
                {
                    var receiveBytes = _udpClient.Receive(ref remoteIpEndPoint);

                    var reader = new BinaryReader(new MemoryStream(receiveBytes, 0, receiveBytes.Length, false));
                    var opCode = (OpCode)reader.ReadUInt16();
                    var length = reader.ReadUInt16();
                    var recvSequenceNumber = reader.ReadUInt64();
                    var sendAck = false;

                    switch (opCode)
                    {
                        case OpCode.Eco:
                            if (ConnectionStatus.InActive == _status)
                            {
                                _status = ConnectionStatus.Connecting;

                                Console.WriteLine("Incoming Establish Connection (ECO) recvSeq: " + recvSequenceNumber);

                                var aecPacket = new Packet((ushort)OpCode.Aec);
                                aecPacket.WriteUInt16(0);
                                aecPacket.WriteUInt64(++_sequenceNumber);
                                var data = aecPacket.GetBytes();

                                _expectedSequenceNumber = recvSequenceNumber + 1;
                                Console.WriteLine("Sending Accept Establish Connection (AEC) Seq: " + _sequenceNumber);

                                _udpClient.Send(data, data.Length, remoteIpEndPoint);
                            }
                            break;

                        case OpCode.Aec:
                            if (ConnectionStatus.Connecting == _status)
                            {
                                Console.WriteLine("Incoming Accept Establish Connection (AEC) recvSeq: " + recvSequenceNumber);
                                _status = ConnectionStatus.Conntected;

                                _expectedSequenceNumber = recvSequenceNumber;
                                Console.WriteLine("Connection established!\n");
                                sendAck = true;
                            }
                            break;

                        case OpCode.Sft:
                            if (ConnectionStatus.Conntected == _status)
                            {
                                receiveBuffer.Clear();
                                _status = ConnectionStatus.TransferData;
                                Console.WriteLine("Incoming Start File Transfer (SFT) recvSeq: " + recvSequenceNumber);

                                sendAck = true;
                            }
                            break;

                        case OpCode.Trd:
                            if (ConnectionStatus.TransferData == _status)
                            {
                                Console.WriteLine("Incoming Transfer data (TRD) recvSeq: " + recvSequenceNumber);

                                sendAck = true;
                            }
                            break;

                        case OpCode.Ack:
                            Console.WriteLine("Incoming Acknowledgement (ACK) recvSeq: " + recvSequenceNumber);
                            if (ConnectionStatus.Connecting == _status)
                            {
                                _status = ConnectionStatus.Conntected;
                                Console.WriteLine("Connection established!\n");
                            }

                            if (ConnectionStatus.TransferData == _status && _expectedSequenceNumber != recvSequenceNumber)
                            {
                                Console.WriteLine("Unexpected Sequence Number: " + recvSequenceNumber + "  Expected: " + _expectedSequenceNumber);
                            }
                            break;
                    }

                    if (sendAck)
                    {
                        var ackPacket = new Packet((ushort)OpCode.Ack);
                        ackPacket.WriteUInt16(0);
                        ackPacket.WriteUInt64(recvSequenceNumber);
                        var data = ackPacket.GetBytes();

                        if (_udpClient.Client.Connected)
                        {
                            _udpClient.Send(data, data.Length);
                        }
                        else
                        {
                            _udpClient.Send(data, data.Length, remoteIpEndPoint);
                        }
                        
                        Console.WriteLine("Sending Acknowledgement (ACK) Seq: " + recvSequenceNumber);
                    }

                    Thread.Sleep(10);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }

            _udpClient.Close();
        }

        public static byte[] ObjectToByteArray(Object obj)
        {
            BinaryFormatter bf = new BinaryFormatter();
            using (var ms = new MemoryStream())
            {
                bf.Serialize(ms, obj);
                return ms.ToArray();
            }
        }

        private void SendThreadFunction(object obj)
        {
            var fileData = ObjectToByteArray(obj);
            var fileDataIndex = 0;

            try
            {
                _udpClient.Connect(_destAddress, _destPort);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            Console.WriteLine("Starting handshake...");
            while (_sendThreadRunning)
            {
                try
                {
                    switch (_status)
                    {
                        case ConnectionStatus.InActive:
                        {
                            _status = ConnectionStatus.Connecting;

                            var conPacket = new Packet((ushort)OpCode.Eco);
                            conPacket.WriteUInt16(0);
                            conPacket.WriteUInt64(++_sequenceNumber);

                            var data = conPacket.GetBytes();
                            Console.WriteLine("Sending Establish Connection (ECO) Seq: " + _sequenceNumber);

                            _udpClient.Send(data, data.Length);
                        }
                        break;

                        case ConnectionStatus.Conntected:
                        {
                            Thread.Sleep(100);
                            _status = ConnectionStatus.TransferData;

                            var sftPacket = new Packet((ushort)OpCode.Sft);
                            sftPacket.WriteUInt16(0);
                            sftPacket.WriteUInt64(++_sequenceNumber);
                            var data = sftPacket.GetBytes();

                            _expectedSequenceNumber = _sequenceNumber;
                            Console.WriteLine("Sending Start File Transfer (SFT) Seq: " + _sequenceNumber);

                            _udpClient.Send(data, data.Length);
                        }
                        break;

                        case ConnectionStatus.TransferData:
                        {
                            if (_expectedSequenceNumber == _sequenceNumber)
                            {
                                if (fileDataIndex < fileData.Length)
                                {
                                    var trdPacket = new Packet((ushort)OpCode.Trd);
                                    var packetSize = fileData.Length - fileDataIndex >= 1464 ? 1464 : fileData.Length - fileDataIndex;
                                    trdPacket.WriteUInt16(packetSize);
                                    fileDataIndex += packetSize;
                                    trdPacket.WriteUInt64(++_sequenceNumber);

                                    var data = trdPacket.GetBytes();
                                    _expectedSequenceNumber = _sequenceNumber;
                                    Console.WriteLine("Sending Transfer data (TRD) Seq: " + _sequenceNumber);

                                    _udpClient.Send(data, data.Length);
                                }
                            }
                        }
                        break;
                    }

                    Thread.Sleep(10);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }

            _udpClient.Close();
        }

        public void Send(string address, int port, byte[] data)
        {
            try
            {
                _destAddress = address;
                _destPort = port;

                if (null != _sendThread && !_sendThread.IsAlive)
                {
                    _sendThreadRunning = true;
                    _sendThread.Start(data);
                }
            }  
            catch (Exception e) 
            {
                Console.WriteLine(e.ToString());
            }   
        }

        public void Open()
        {
            try
            {
                if (null != _receiveThread && !_receiveThread.IsAlive)
                {
                    _receiveThreadRunning = true;
                    _receiveThread.Start();
                }
            }  
            catch (Exception e) 
            {
                Console.WriteLine(e.ToString());
            }   
        }

        public void Close()
        {
            try
            {
                if (null != _receiveThread && _receiveThread.IsAlive)
                {
                    _receiveThreadRunning = false;
                    _receiveThread.Abort();
                }
            }  
            catch (Exception e) 
            {
                Console.WriteLine(e.ToString());
            }
        }
    }
}
