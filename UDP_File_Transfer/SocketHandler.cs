using System;
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
        private Stopwatch _resendTimer;

        private ulong _sequenceNumber;
        private ulong _expectedSequenceNumber;
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
            _sequenceNumber = sequenceNumber;
            _resendDelay = resendDelay;

            _receiveThreadRunning = false;
            _sendThreadRunning = false;

            _receiveThread = new Thread(ReceiveThreadFunction);
            _sendThread = new Thread(SendThreadFunction);

            _udpClient = new UdpClient(aPort);
            _resendTimer = new Stopwatch();
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
                    var recvSequenceNumber = reader.ReadUInt64();

                    bool sendAck;
                    if (_status == ConnectionStatus.InActive || recvSequenceNumber == _expectedSequenceNumber)
                    {
                        sendAck = false;

                        switch (opCode)
                        {
                            case OpCode.Eco:
                                if (ConnectionStatus.InActive == _status)
                                {
                                    _status = ConnectionStatus.Connecting;

                                    Console.WriteLine("Incoming Establish Connection (ECO) recvSeq: " +
                                                      recvSequenceNumber);

                                    var aecPacket = new Packet((ushort) OpCode.Aec);
                                    aecPacket.WriteUInt16(0);
                                    aecPacket.WriteUInt64(++_sequenceNumber);
                                    var data = aecPacket.GetBytes();

                                    _expectedSequenceNumber = recvSequenceNumber + 1;
                                    Console.WriteLine("Sending Accept Establish Connection (AEC) Seq: " +
                                                      _sequenceNumber);

                                    _udpClient.Send(data, data.Length, remoteIpEndPoint);
                                }

                                break;

                            case OpCode.Aec:
                                if (ConnectionStatus.InActive == _status)
                                {
                                    Console.WriteLine("Incoming Accept Establish Connection (AEC) recvSeq: " +
                                                      recvSequenceNumber);
                                    _status = ConnectionStatus.Connected;

                                    _expectedSequenceNumber = recvSequenceNumber;
                                    Console.WriteLine("Connection established!\n");
                                    sendAck = true;
                                }

                                break;

                            case OpCode.Sft:
                                if (ConnectionStatus.Connected == _status)
                                {
                                    recvFile = new FileStream("test1.zip", FileMode.Append);

                                    _status = ConnectionStatus.TransferData;
                                    Console.WriteLine("Incoming Start File Transfer (SFT) recvSeq: " +
                                                      recvSequenceNumber);

                                    sendAck = true;
                                }

                                break;

                            case OpCode.Trd:
                                if (ConnectionStatus.TransferData == _status)
                                {
                                    Console.WriteLine("Incoming Transfer data (TRD) recvSeq: " + recvSequenceNumber);
                                    var data = reader.ReadBytes(length);

                                    recvFile?.Write(data, 0, data.Length);

                                    sendAck = true;
                                }

                                break;

                            case OpCode.Eft:
                                if (ConnectionStatus.TransferData == _status)
                                {
                                    Console.WriteLine("Incoming End File Transfer (EFT) recvSeq: " +
                                                      recvSequenceNumber);
                                    recvFile?.Close();
                                }

                                break;

                            case OpCode.Fin:
                                if (_status >= ConnectionStatus.Connected && _status != ConnectionStatus.Stop)
                                {
                                    Console.WriteLine("Incoming Finalize (FIN) recvSeq: " + recvSequenceNumber);

                                    _status = ConnectionStatus.Finalize;

                                    var finPacket = new Packet((ushort) OpCode.Fin);
                                    finPacket.WriteUInt16(0);
                                    finPacket.WriteUInt64(++_sequenceNumber);
                                    var data = finPacket.GetBytes();

                                    Console.WriteLine("Sending Finalize (FIN) Seq: " + _sequenceNumber);

                                    _udpClient.Send(data, data.Length, remoteIpEndPoint);
                                }

                                break;

                            case OpCode.Ack:
                                Console.WriteLine("Incoming Acknowledgement (ACK) recvSeq: " + recvSequenceNumber);

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
                                        if (_expectedSequenceNumber != recvSequenceNumber)
                                        {
                                            Console.WriteLine("Unexpected Sequence Number: " + recvSequenceNumber +
                                                              "  Expected: " + _expectedSequenceNumber);
                                        }
                                        else
                                        {
                                            lock (_resendTimer)
                                            {
                                                _resendTimer.Reset();
                                            }
                                        }

                                        break;
                                    case ConnectionStatus.Finalize:
                                    case ConnectionStatus.Stop:
                                        if (_expectedSequenceNumber == recvSequenceNumber)
                                        {
                                            _resendTimer.Reset();
                                            _receiveThreadRunning = false;
                                        }

                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }

                                _recvSequenceNumber = recvSequenceNumber;
                                break;
                        }
                    }
                    else
                    {
                        recvSequenceNumber = _expectedSequenceNumber;
                        sendAck = true;
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
            var tempDataIndex = 0;

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
                            if (!_resendTimer.IsRunning || _resendTimer.ElapsedMilliseconds > _resendDelay)
                            {
                                var conPacket = new Packet((ushort)OpCode.Eco);
                                conPacket.WriteUInt16(0);
                                conPacket.WriteUInt64(++_sequenceNumber);

                                var data = conPacket.GetBytes();
                                Console.WriteLine("Sending Establish Connection (ECO) Seq: " + _sequenceNumber);

                                _resendTimer.Restart();
                                _udpClient.Send(data, data.Length);
                            }

                            break;

                        case ConnectionStatus.Connected:
                        {
                            if (!_resendTimer.IsRunning || _resendTimer.ElapsedMilliseconds > _resendDelay)
                            {
                                var sftPacket = new Packet((ushort)OpCode.Sft);
                                sftPacket.WriteUInt16(0);
                                sftPacket.WriteUInt64(++_sequenceNumber);
                                var data = sftPacket.GetBytes();

                                _expectedSequenceNumber = _sequenceNumber;
                                Console.WriteLine("Sending Start File Transfer (SFT) Seq: " + _sequenceNumber);

                                _resendTimer.Restart();
                                _udpClient.Send(data, data.Length);
                            }
                        }
                        break;

                        case ConnectionStatus.TransferData:
                        {
                            lock (_resendTimer)
                            {
                                if (!_resendTimer.IsRunning)
                                {
                                    tempDataIndex = fileDataIndex;
                                    _resendTimer.Restart();
                                }
                                else if (_resendTimer.ElapsedMilliseconds > _resendDelay)
                                {
                                    fileDataIndex = tempDataIndex;
                                }
                            }

                            if (fileDataIndex < fileData.Length)
                            {
                                var trdPacket = new Packet((ushort) OpCode.Trd);
                                var packetSize = fileData.Length - fileDataIndex >= 1464
                                    ? 1464
                                    : fileData.Length - fileDataIndex;
                                trdPacket.WriteUInt16(packetSize);
                                trdPacket.WriteUInt64(++_sequenceNumber);
                                trdPacket.WriteInt8Array(fileData, fileDataIndex, packetSize);

                                var data = trdPacket.GetBytes();
                                _expectedSequenceNumber = _sequenceNumber;
                                Console.WriteLine("Sending Transfer data (TRD) Seq: " + _sequenceNumber);

                                _udpClient.Send(data, data.Length);

                                fileDataIndex += packetSize;
                            }
                            else
                            {
                                _status = ConnectionStatus.Stop;

                                var eftPacket = new Packet((ushort) OpCode.Eft);
                                eftPacket.WriteUInt16(0);
                                eftPacket.WriteUInt64(++_sequenceNumber);

                                var data = eftPacket.GetBytes();
                                _expectedSequenceNumber = _sequenceNumber;
                                Console.WriteLine("Sending End File Transfer (EFT) Seq: " + _sequenceNumber);

                                _udpClient.Send(data, data.Length);

                                var finPacket = new Packet((ushort) OpCode.Fin);
                                finPacket.WriteUInt16(0);
                                finPacket.WriteUInt64(++_sequenceNumber);
                                data = finPacket.GetBytes();

                                Console.WriteLine("Sending Finalize (FIN) Seq: " + _sequenceNumber);

                                _udpClient.Send(data, data.Length);

                                _sendThreadRunning = false;
                            }

                        }
                        break;
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
