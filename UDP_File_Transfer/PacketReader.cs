using System.IO;

namespace UDP_File_Transfer
{
    internal class PacketReader : BinaryReader
    {
        byte[] _mInput;

        public PacketReader(byte[] input)
            : base(new MemoryStream(input, false))
        {
            _mInput = input;
        }

        public PacketReader(byte[] input, int index, int count)
            : base(new MemoryStream(input, index, count, false))
        {
            _mInput = input;
        }
    }
}