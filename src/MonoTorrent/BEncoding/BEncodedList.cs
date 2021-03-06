using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace MonoTorrent.BEncoding
{
    /// <summary>
    ///     Class representing a BEncoded list
    /// </summary>
    public class BEncodedList : BEncodedValue, IList<BEncodedValue>
    {
        #region Member Variables

        private readonly List<BEncodedValue> list;

        #endregion

        #region Helper Methods

        /// <summary>
        ///     Returns the size of the list in bytes
        /// </summary>
        /// <returns></returns>
        public override int LengthInBytes()
        {
            var length = 0;

            length += 1; // Lists start with 'l'
            for (var i = 0; i < list.Count; i++)
                length += list[i].LengthInBytes();

            length += 1; // Lists end with 'e'
            return length;
        }

        #endregion

        #region Constructors

        /// <summary>
        ///     Create a new BEncoded List with default capacity
        /// </summary>
        public BEncodedList()
            : this(new List<BEncodedValue>())
        {
        }

        /// <summary>
        ///     Create a new BEncoded List with the supplied capacity
        /// </summary>
        /// <param name="capacity">The initial capacity</param>
        public BEncodedList(int capacity)
            : this(new List<BEncodedValue>(capacity))
        {
        }

        public BEncodedList(IEnumerable<BEncodedValue> list)
        {
            if (list == null)
                throw new ArgumentNullException("list");

            this.list = new List<BEncodedValue>(list);
        }

        private BEncodedList(List<BEncodedValue> value)
        {
            list = value;
        }

        #endregion

        #region Encode/Decode Methods

        /// <summary>
        ///     Encodes the list to a byte[]
        /// </summary>
        /// <param name="buffer">The buffer to encode the list to</param>
        /// <param name="offset">The offset to start writing the data at</param>
        /// <returns></returns>
        public override int Encode(byte[] buffer, int offset)
        {
            var written = 0;
            buffer[offset] = (byte) 'l';
            written++;
            for (var i = 0; i < list.Count; i++)
                written += list[i].Encode(buffer, offset + written);
            buffer[offset + written] = (byte) 'e';
            written++;
            return written;
        }

        /// <summary>
        ///     Decodes a BEncodedList from the given StreamReader
        /// </summary>
        /// <param name="reader"></param>
        internal override void DecodeInternal(RawReader reader)
        {
            if (reader.ReadByte() != 'l') // Remove the leading 'l'
                throw new BEncodingException("Invalid data found. Aborting");

            while ((reader.PeekByte() != -1) && (reader.PeekByte() != 'e'))
                list.Add(Decode(reader));

            if (reader.ReadByte() != 'e') // Remove the trailing 'e'
                throw new BEncodingException("Invalid data found. Aborting");
        }

        #endregion

        #region Overridden Methods

        public override bool Equals(object obj)
        {
            var other = obj as BEncodedList;

            if (other == null)
                return false;

            for (var i = 0; i < list.Count; i++)
                if (!list[i].Equals(other.list[i]))
                    return false;

            return true;
        }


        public override int GetHashCode()
        {
            var result = 0;
            for (var i = 0; i < list.Count; i++)
                result ^= list[i].GetHashCode();

            return result;
        }


        public override string ToString()
        {
            return Encoding.UTF8.GetString(Encode());
        }

        #endregion

        #region IList methods

        public void Add(BEncodedValue item)
        {
            list.Add(item);
        }

        public void AddRange(IEnumerable<BEncodedValue> collection)
        {
            list.AddRange(collection);
        }

        public void Clear()
        {
            list.Clear();
        }

        public bool Contains(BEncodedValue item)
        {
            return list.Contains(item);
        }

        public void CopyTo(BEncodedValue[] array, int arrayIndex)
        {
            list.CopyTo(array, arrayIndex);
        }

        public int Count
        {
            get { return list.Count; }
        }

        public int IndexOf(BEncodedValue item)
        {
            return list.IndexOf(item);
        }

        public void Insert(int index, BEncodedValue item)
        {
            list.Insert(index, item);
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public bool Remove(BEncodedValue item)
        {
            return list.Remove(item);
        }

        public void RemoveAt(int index)
        {
            list.RemoveAt(index);
        }

        public BEncodedValue this[int index]
        {
            get { return list[index]; }
            set { list[index] = value; }
        }

        public IEnumerator<BEncodedValue> GetEnumerator()
        {
            return list.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion
    }
}