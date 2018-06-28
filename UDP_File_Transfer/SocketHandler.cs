using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        Connected,
        TransferData,
        Finalize,
        Stop
    }

    public class SocketHandler
    {
        private ConnectionStatus _status;
        private readonly Stopwatch _resendTimer;
        private readonly Mutex _m;

        private ulong _mySequenceNumber;
        private ulong _expectedAckNumber;
        private ulong _expectedSequenceNumber;
        private ulong _recvAckNumber;
        private bool _allowNextPacket;
        private ulong _recvSequenceNumber;

        private readonly Thread _receiveThread;
        private readonly Thread _sendThread;
        private readonly int _resendDelay;

        private readonly UdpClient _udpClient;

        private bool _receiveThreadRunning;
        private bool _sendThreadRunning;

        private readonly int _myPort;

        private string _destAddress;
        private int _destPort;

        public SocketHandler(int aPort, ulong sequenceNumber, int resendDelay)
        {
            _myPort = aPort;
            _mySequenceNumber = sequenceNumber;
            _resendDelay = resendDelay;
            
            _receiveThreadRunning = false;
            _sendThreadRunning = false;

            _receiveThread = new Thread(ReceiveThreadFunction);
            _sendThread = new Thread(SendThreadFunction);

            _udpClient = new UdpClient(aPort);
            _resendTimer = new Stopwatch();

            _expectedAckNumber = 0;
            _allowNextPacket = true;
            _m = new Mutex();
        }

        private void ReceiveThreadFunction(object obj)
        {
            FileStream recvFile = null;

            var remoteIpEndPoint = new IPEndPoint(IPAddress.Any, _myPort);
            while (_receiveThreadRunning)
            {
                try
                {
                    var receiveBytes = _udpClient.Receive(ref remoteIpEndPoint);

                    var reader = new BinaryReader(new MemoryStream(receiveBytes, 0, receiveBytes.Length, false));
                    var opCode = (OpCode)reader.ReadUInt16();
                    var length = reader.ReadUInt16();
                    _recvSequenceNumber = reader.ReadUInt64();
                    var sendAck = false;

                    {
                        switch (opCode)
                        {
                            case OpCode.Eco:
                                if (ConnectionStatus.InActive == _status)
                                {
                                    _status = ConnectionStatus.Connecting;

                                    Console.WriteLine("Incoming Establish Connection (ECO) recvSeq: " +
                                                      _recvSequenceNumber);

                                    var aecPacket = new Packet((ushort) OpCode.Aec);
                                    aecPacket.WriteUInt16(0);
                                    aecPacket.WriteUInt64(_mySequenceNumber);
                                    var data = aecPacket.GetBytes();

                                    Console.WriteLine("Sending Accept Establish Connection (AEC) Seq: " +
                                                      _mySequenceNumber);
                                    _mySequenceNumber++;
                                    _expectedSequenceNumber = _recvSequenceNumber + 1;

                                    _udpClient.Send(data, data.Length, remoteIpEndPoint);
                                }

                                break;

                            case OpCode.Aec:
                                if (ConnectionStatus.Connecting == _status)
                                {
                                    Console.WriteLine("Incoming Accept Establish Connection (AEC) recvSeq: " +
                                                      _recvSequenceNumber);
                                    _status = ConnectionStatus.Connected;

                                    lock (_m)
                                    {
                                        _recvAckNumber = _mySequenceNumber - 1;
                                    }
                                    _expectedAckNumber = _recvSequenceNumber;
                                    Console.WriteLine("Connection established!\n");
                                    sendAck = true;
                                }

                                break;

                            case OpCode.Sft:
                                if (ConnectionStatus.Connected == _status)
                                {
                                    recvFile = new FileStream("test1.zip", FileMode.Append);

                                    _status = ConnectionStatus.TransferData;
                                    Console.WriteLine("Incoming Start File Transfer (SFT) recvSeq: " + _recvSequenceNumber);

                                    sendAck = true;
                                }

                                break;

                            case OpCode.Trd:
                                if (ConnectionStatus.TransferData == _status)
                                {
                                    Console.WriteLine("Incoming Transfer data (TRD) recvSeq: " + _recvSequenceNumber);
                                    if (_recvSequenceNumber == _expectedSequenceNumber)
                                    {
                                        var data = reader.ReadBytes(length);
                                        recvFile?.Write(data, 0, data.Length);
                                    }
                                    else
                                    {
                                        Console.WriteLine("Unexpected recvSeq: " + _recvSequenceNumber + " expect: " +
                                                          _expectedSequenceNumber);
                                    }

                                    sendAck = true;
                                }

                                break;

                            case OpCode.Eft:
                                if (ConnectionStatus.TransferData == _status)
                                {
                                    Console.WriteLine(
                                        "Incoming End File Transfer (EFT) recvSeq: " + _recvSequenceNumber);
                                    recvFile?.Close();
                                }

                                break;

                            case OpCode.Fin:
                                if (_status >= ConnectionStatus.Connected && _status != ConnectionStatus.Stop)
                                {
                                    Console.WriteLine("Incoming Finalize (FIN) recvSeq: " + _recvSequenceNumber);

                                    _status = ConnectionStatus.Finalize;

                                    var finPacket = new Packet((ushort) OpCode.Fin);
                                    finPacket.WriteUInt16(0);
                                    finPacket.WriteUInt64(_mySequenceNumber);
                                    var data = finPacket.GetBytes();

                                    Console.WriteLine("Sending Finalize (FIN) Seq: " + _mySequenceNumber);
                                    _mySequenceNumber++;

                                    _udpClient.Send(data, data.Length, remoteIpEndPoint);
                                }

                                break;

                            case OpCode.Ack:
                                Console.WriteLine("Incoming Acknowledgement (ACK) recvSeq: " + _recvSequenceNumber);
                                lock (_m)
                                {
                                    _recvAckNumber = _recvSequenceNumber;
                                } 

                                switch (_status)
                                {
                                    case ConnectionStatus.InActive:
                                        break;
                                    case ConnectionStatus.Connecting:
                                        _resendTimer.Reset();
                                        _status = ConnectionStatus.Connected;
                                        Console.WriteLine("Connection established!\n");
                                        break;
                                    case ConnectionStatus.Connected:
                                        _resendTimer.Reset();
                                        _status = ConnectionStatus.TransferData;
                                        Console.WriteLine("Ready for File transfer!");
                                        break;
                                    case ConnectionStatus.TransferData:
                                        if (_expectedAckNumber != _recvSequenceNumber)
                                        {
                                            Console.WriteLine("Unexpected Sequence Number: " + _recvSequenceNumber +
                                                              "  Expected: " + _expectedAckNumber);
                                        }
                                        else
                                        {
                                            _resendTimer.Reset();
                                        }

                                        break;
                                    case ConnectionStatus.Finalize:
                                    case ConnectionStatus.Stop:
                                        _resendTimer.Reset();
                                        _receiveThreadRunning = false;
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }

                                break;
                        }
                    }

                    if (sendAck)
                    {
                        var ackPacket = new Packet((ushort)OpCode.Ack);
                        ackPacket.WriteUInt16(0);
                        ackPacket.WriteUInt64(_recvSequenceNumber);
                        _expectedSequenceNumber = _recvSequenceNumber + 1;
                        var data = ackPacket.GetBytes();

                        if (_udpClient.Client.Connected)
                        {
                            _udpClient.Send(data, data.Length);
                        }
                        else
                        {
                            _udpClient.Send(data, data.Length, remoteIpEndPoint);
                        }
                        
                        Console.WriteLine("Sending Acknowledgement (ACK) Seq: " + _recvSequenceNumber);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }

            _udpClient.Close();
            Console.WriteLine("UDPClient closed!");
        }

        public static byte[] ObjectToByteArray(Object obj)
        {
            var bf = new BinaryFormatter();
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

            var sendBuffer = new List<KeyValuePair<ulong, Packet>>();
            Console.WriteLine("Starting handshake...");

            while (_sendThreadRunning)
            {
                try
                {
                    lock (_m)
                    {
                        if (_recvAckNumber == _expectedAckNumber)
                        {
                            _allowNextPacket = true;
                        }
                    }
                   
                    if (_allowNextPacket)
                    {
                        switch (_status)
                        {
                            case ConnectionStatus.InActive:
                            {
                                var conPacket = new Packet((ushort) OpCode.Eco);
                                conPacket.WriteUInt16(0);
                                conPacket.WriteUInt64(_mySequenceNumber);
                                sendBuffer.Add(new KeyValuePair<ulong, Packet>(_mySequenceNumber, conPacket));
                                
                                Console.WriteLine("Sending Establish Connection (ECO) Seq: " + _mySequenceNumber);
                                _status = ConnectionStatus.Connecting;
                                _mySequenceNumber++;
                            }
                                break;

                            case ConnectionStatus.Connected:
                            {
                                var sftPacket = new Packet((ushort) OpCode.Sft);
                                sftPacket.WriteUInt16(0);
                                sftPacket.WriteUInt64(_mySequenceNumber);
                                sendBuffer.Add(new KeyValuePair<ulong, Packet>(_mySequenceNumber, sftPacket));
                                
                                Console.WriteLine("Sending Start File Transfer (SFT) Seq: " + _mySequenceNumber);
                                _mySequenceNumber++;
                            }
                                break;

                            case ConnectionStatus.TransferData:
                            {
                                 if (fileDataIndex < fileData.Length)
                                    {
                                        var trdPacket = new Packet((ushort) OpCode.Trd);
                                        var packetSize = fileData.Length - fileDataIndex >= 1464
                                            ? 1464
                                            : fileData.Length - fileDataIndex;
                                        trdPacket.WriteUInt16(packetSize);
                                        trdPacket.WriteUInt64(_mySequenceNumber);
                                        trdPacket.WriteInt8Array(fileData, fileDataIndex, packetSize);
                                        sendBuffer.Add(new KeyValuePair<ulong, Packet>(_mySequenceNumber, trdPacket));

                                        Console.WriteLine("Sending Transfer data (TRD) Seq: " + _mySequenceNumber);

                                        _mySequenceNumber++;
                                        fileDataIndex += packetSize;
                                    }
                                    else
                                    {
                                        _status = ConnectionStatus.Finalize;

                                        var eftPacket = new Packet((ushort) OpCode.Eft);
                                        eftPacket.WriteUInt16(0);
                                        eftPacket.WriteUInt64(_mySequenceNumber);
                                        sendBuffer.Add(new KeyValuePair<ulong, Packet>(_mySequenceNumber, eftPacket));
                                        Console.WriteLine("Sending End File Transfer (EFT) Seq: " + _mySequenceNumber);
                                        _mySequenceNumber++;

                                        var finPacket = new Packet((ushort) OpCode.Fin);
                                        finPacket.WriteUInt16(0);
                                        finPacket.WriteUInt64(_mySequenceNumber);
                                        sendBuffer.Add(new KeyValuePair<ulong, Packet>(_mySequenceNumber, finPacket));
                                        Console.WriteLine("Sending Finalize (FIN) Seq: " + _mySequenceNumber);
                                        _mySequenceNumber++;

                                        _sendThreadRunning = false;
                                    }
                            }
                                break;
                        }
                    }

                    lock (_m)
                    {
                        foreach (var kvp in sendBuffer)
                        {
                            if (kvp.Key == _recvAckNumber)
                            {
                                sendBuffer.Remove(kvp);
                                _allowNextPacket = true;
                                break;
                            }
                        }
                    }

                    if (sendBuffer.Count > 0)
                    {
                        _allowNextPacket = false;

                        if (!_resendTimer.IsRunning || _resendTimer.ElapsedMilliseconds > _resendDelay)
                        {
                            var packet = sendBuffer[0].Value;
                            var data = packet.GetBytes();

                            _resendTimer.Restart();
                            _expectedAckNumber = sendBuffer[0].Key;

                            _udpClient.Send(data, data.Length);
                            Thread.Sleep(5);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                }
            }
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
