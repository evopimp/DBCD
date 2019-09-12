﻿using DBCD.IO.Common;
using DBCD.IO.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DBCD.IO.Writers
{
    class WDC3RowSerializer<T> : IDBRowSerializer<T> where T : class
    {
        public IDictionary<int, BitWriter> Records { get; private set; }

        private readonly BaseWriter<T> m_writer;
        private readonly FieldMetaData[] m_fieldMeta;
        private readonly ColumnMetaData[] m_columnMeta;
        private readonly List<Value32[]>[] m_palletData;
        private readonly Dictionary<int, Value32>[] m_commonData;

        private static readonly Value32Comparer Value32Comparer = new Value32Comparer();


        public WDC3RowSerializer(BaseWriter<T> writer)
        {
            m_writer = writer;
            m_fieldMeta = m_writer.Meta;
            m_columnMeta = m_writer.ColumnMeta;
            m_palletData = m_writer.PalletData;
            m_commonData = m_writer.CommonData;

            Records = new Dictionary<int, BitWriter>();
        }

        public void Serialize(IDictionary<int, T> rows)
        {
            foreach (var row in rows)
                Serialize(row.Key, row.Value);
        }

        public void Serialize(int id, T row)
        {
            BitWriter bitWriter = new BitWriter(m_writer.RecordSize);

            int indexFieldOffSet = 0;
            for (int i = 0; i < m_writer.FieldCache.Length; i++)
            {
                FieldCache<T> info = m_writer.FieldCache[i];

                if (i == m_writer.IdFieldIndex && m_writer.Flags.HasFlagExt(DB2Flags.Index))
                {
                    indexFieldOffSet++;
                    continue;
                }

                int fieldIndex = i - indexFieldOffSet;

                // reference data field
                if (fieldIndex >= m_writer.Meta.Length)
                {
                    m_writer.ReferenceData.Add((int)Convert.ChangeType(info.Getter(row), typeof(int)));
                    continue;
                }

                if (info.IsArray)
                {
                    if (arrayWriters.TryGetValue(info.Field.FieldType, out var writer))
                        writer(bitWriter, m_writer, m_fieldMeta[fieldIndex], m_columnMeta[fieldIndex], m_palletData[fieldIndex], m_commonData[fieldIndex], (Array)info.Getter(row));
                    else
                        throw new Exception("Unhandled array type: " + typeof(T).Name);
                }
                else
                {
                    if (simpleWriters.TryGetValue(info.Field.FieldType, out var writer))
                        writer(id, bitWriter, m_writer, m_fieldMeta[fieldIndex], m_columnMeta[fieldIndex], m_palletData[fieldIndex], m_commonData[fieldIndex], info.Getter(row));
                    else
                        throw new Exception("Unhandled field type: " + typeof(T).Name);
                }
            }

            // pad to record size
            if (!m_writer.Flags.HasFlagExt(DB2Flags.Sparse))
                bitWriter.Resize(m_writer.RecordSize);
            else
                bitWriter.ResizeToMultiple(4);

            Records[id] = bitWriter;
        }

        public void GetCopyRows()
        {
            var copydata = Records.GroupBy(x => x.Value).Where(x => x.Count() > 1).ToArray();
            foreach (var copygroup in copydata)
            {
                int key = copygroup.First().Key;
                foreach (var copy in copygroup.Skip(1))
                    m_writer.CopyData[copy.Key] = key;
            }
        }

        public void UpdateStringOffsets(IDictionary<int, T> rows)
        {
            if (m_writer.Flags.HasFlagExt(DB2Flags.Sparse) || m_writer.StringTableSize <= 1)
                return;

            int indexFieldOffSet = 0;
            var fieldInfos = new Dictionary<int, FieldCache<T>>();
            for (int i = 0; i < m_writer.FieldCache.Length; i++)
            {
                if (i == m_writer.IdFieldIndex && m_writer.Flags.HasFlagExt(DB2Flags.Index))
                    indexFieldOffSet++;
                else if (m_writer.FieldCache[i].Field.FieldType == typeof(string))
                    fieldInfos[i - indexFieldOffSet] = m_writer.FieldCache[i];
                else if (m_writer.FieldCache[i].Field.FieldType == typeof(string[]))
                    fieldInfos[i - indexFieldOffSet] = m_writer.FieldCache[i];
            }

            if (fieldInfos.Count == 0)
                return;

            int recordOffset = (Records.Count - m_writer.CopyData.Count) * m_writer.RecordSize;
            int fieldOffset = 0;

            foreach (var record in Records)
            {
                // skip copy records
                if (m_writer.CopyData.ContainsKey(record.Key))
                    continue;

                foreach (var fieldInfo in fieldInfos)
                {
                    int index = fieldInfo.Key;
                    var info = fieldInfo.Value;

                    var columnMeta = m_columnMeta[index];
                    if (columnMeta.CompressionType != CompressionType.None)
                        throw new Exception("CompressionType != CompressionType.None");

                    int bitSize = 32 - m_fieldMeta[index].Bits;
                    if (bitSize <= 0)
                        bitSize = columnMeta.Immediate.BitWidth;

                    if (info.IsArray)
                    {
                        var array = (string[])info.Getter(rows[record.Key]);
                        for (int i = 0; i < array.Length; i++)
                        {
                            fieldOffset = m_writer.StringTable[array[i]] + recordOffset - (columnMeta.RecordOffset / 8 * i);
                            record.Value.Write(fieldOffset, bitSize, columnMeta.RecordOffset + (i * bitSize));
                        }
                    }
                    else
                    {
                        fieldOffset = m_writer.StringTable[(string)info.Getter(rows[record.Key])] + recordOffset - (columnMeta.RecordOffset / 8);
                        record.Value.Write(fieldOffset, bitSize, columnMeta.RecordOffset);
                    }
                }

                recordOffset -= m_writer.RecordSize;
            }
        }


        private static Dictionary<Type, Action<int, BitWriter, BaseWriter<T>, FieldMetaData, ColumnMetaData, List<Value32[]>, Dictionary<int, Value32>, object>> simpleWriters = new Dictionary<Type, Action<int, BitWriter, BaseWriter<T>, FieldMetaData, ColumnMetaData, List<Value32[]>, Dictionary<int, Value32>, object>>
        {
            [typeof(long)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<long>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(float)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<float>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(int)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<int>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(uint)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<uint>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(short)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<short>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(ushort)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<ushort>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(sbyte)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<sbyte>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(byte)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) => WriteFieldValue<byte>(id, data, fieldMeta, columnMeta, palletData, commonData, value),
            [typeof(string)] = (id, data, writer, fieldMeta, columnMeta, palletData, commonData, value) =>
            {
                if (writer.Flags.HasFlagExt(DB2Flags.Sparse))
                    data.WriteCString((string)value);
                else
                    WriteFieldValue<int>(id, data, fieldMeta, columnMeta, palletData, commonData, writer.InternString((string)value));
            }
        };

        private static Dictionary<Type, Action<BitWriter, BaseWriter<T>, FieldMetaData, ColumnMetaData, List<Value32[]>, Dictionary<int, Value32>, Array>> arrayWriters = new Dictionary<Type, Action<BitWriter, BaseWriter<T>, FieldMetaData, ColumnMetaData, List<Value32[]>, Dictionary<int, Value32>, Array>>
        {
            [typeof(ulong[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<ulong>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(long[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<long>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(float[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<float>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(int[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<int>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(uint[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<uint>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(ulong[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<ulong>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(ushort[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<ushort>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(short[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<short>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(byte[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<byte>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(sbyte[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<sbyte>(data, fieldMeta, columnMeta, palletData, commonData, array),
            [typeof(string[])] = (data, writer, fieldMeta, columnMeta, palletData, commonData, array) => WriteFieldValueArray<int>(data, fieldMeta, columnMeta, palletData, commonData, (array as string[]).Select(x => writer.InternString(x)).ToArray()),
        };

        private static void WriteFieldValue<TType>(int Id, BitWriter r, FieldMetaData fieldMeta, ColumnMetaData columnMeta, List<Value32[]> palletData, Dictionary<int, Value32> commonData, object value) where TType : unmanaged
        {
            switch (columnMeta.CompressionType)
            {
                case CompressionType.None:
                    {
                        int bitSize = 32 - fieldMeta.Bits;
                        if (bitSize <= 0)
                            bitSize = columnMeta.Immediate.BitWidth;

                        r.Write((TType)value, bitSize);
                        break;
                    }
                case CompressionType.Immediate:
                case CompressionType.SignedImmediate:
                    {
                        r.Write((TType)value, columnMeta.Immediate.BitWidth);
                        break;
                    }
                case CompressionType.Common:
                    {
                        if (!columnMeta.Common.DefaultValue.GetValue<TType>().Equals(value))
                            commonData.Add(Id, Value32.Create((TType)value));
                        break;
                    }
                case CompressionType.Pallet:
                    {
                        Value32[] array = new[] { Value32.Create((TType)value) };

                        int palletIndex = palletData.FindIndex(x => Value32Comparer.Equals(array, x));
                        if (palletIndex == -1)
                        {
                            palletIndex = palletData.Count;
                            palletData.Add(array);
                        }

                        r.Write(palletIndex, columnMeta.Pallet.BitWidth);
                        break;
                    }
            }
        }

        private static void WriteFieldValueArray<TType>(BitWriter r, FieldMetaData fieldMeta, ColumnMetaData columnMeta, List<Value32[]> palletData, Dictionary<int, Value32> commonData, Array value) where TType : unmanaged
        {
            switch (columnMeta.CompressionType)
            {
                case CompressionType.None:
                    {
                        int bitSize = 32 - fieldMeta.Bits;
                        if (bitSize <= 0)
                            bitSize = columnMeta.Immediate.BitWidth;

                        for (int i = 0; i < value.Length; i++)
                            r.Write((TType)value.GetValue(i), bitSize);

                        break;
                    }
                case CompressionType.PalletArray:
                    {
                        // get data
                        Value32[] array = new Value32[value.Length];
                        for (int i = 0; i < value.Length; i++)
                            array[i] = Value32.Create((TType)value.GetValue(i));

                        int palletIndex = palletData.FindIndex(x => Value32Comparer.Equals(array, x));
                        if (palletIndex == -1)
                        {
                            palletIndex = palletData.Count;
                            palletData.Add(array);
                        }

                        r.Write(palletIndex, columnMeta.Pallet.BitWidth);
                        break;
                    }
            }
        }
    }

    class WDC3Writer<T> : BaseWriter<T> where T : class
    {
        private const int HeaderSize = 72;
        private const uint WDC3FmtSig = 0x33434457; // WDC3

        public WDC3Writer(WDC3Reader reader, IDictionary<int, T> storage, Stream stream) : base(reader)
        {
            // always 2 empties
            StringTableSize++;

            WDC3RowSerializer<T> serializer = new WDC3RowSerializer<T>(this);
            serializer.Serialize(storage);
            serializer.GetCopyRows();
            serializer.UpdateStringOffsets(storage);

            RecordsCount = serializer.Records.Count - m_copyData.Count;

            var (commonDataSize, palletDataSize, referenceDataSize) = GetDataSizes();

            using (var writer = new BinaryWriter(stream))
            {
                int minIndex = storage.Keys.Min();
                int maxIndex = storage.Keys.Max();

                writer.Write(WDC3FmtSig);
                writer.Write(RecordsCount);
                writer.Write(FieldsCount);
                writer.Write(RecordSize);
                writer.Write(StringTableSize);
                writer.Write(reader.TableHash);
                writer.Write(reader.LayoutHash);
                writer.Write(minIndex);
                writer.Write(maxIndex);
                writer.Write(reader.Locale);
                writer.Write((ushort)Flags);
                writer.Write((ushort)IdFieldIndex);

                writer.Write(FieldsCount); // totalFieldCount
                writer.Write(reader.PackedDataOffset);
                writer.Write(m_referenceData.Count > 0 ? 1 : 0); // RelationshipColumnCount
                writer.Write(m_columnMeta.Length * 24); // ColumnMetaDataSize
                writer.Write(commonDataSize);
                writer.Write(palletDataSize);
                writer.Write(1); // sections count

                if (storage.Count == 0)
                    return;

                // section header
                int fileOffset = HeaderSize + (m_meta.Length * 4) + (m_columnMeta.Length * 24) + Unsafe.SizeOf<SectionHeaderWDC3>() + palletDataSize + commonDataSize;

                writer.Write(0UL); // TactKeyLookup
                writer.Write(fileOffset); // FileOffset
                writer.Write(RecordsCount); // NumRecords
                writer.Write(StringTableSize);
                writer.Write(0); // OffsetRecordsEndOffset
                writer.Write(RecordsCount * 4); // IndexDataSize
                writer.Write(referenceDataSize); // ParentLookupDataSize
                writer.Write(Flags.HasFlagExt(DB2Flags.Sparse) ? RecordsCount : 0); // OffsetMapIDCount
                writer.Write(m_copyData.Count); // CopyTableCount

                // field meta
                writer.WriteArray(m_meta);

                // column meta data
                writer.WriteArray(m_columnMeta);

                // pallet data
                for (int i = 0; i < m_columnMeta.Length; i++)
                {
                    if (m_columnMeta[i].CompressionType == CompressionType.Pallet || m_columnMeta[i].CompressionType == CompressionType.PalletArray)
                    {
                        foreach (var palletData in m_palletData[i])
                            writer.WriteArray(palletData);
                    }
                }

                // common data
                for (int i = 0; i < m_columnMeta.Length; i++)
                {
                    if (m_columnMeta[i].CompressionType == CompressionType.Common)
                    {
                        foreach (var commondata in m_commonData[i])
                        {
                            writer.Write(commondata.Key);
                            writer.Write(commondata.Value.GetValue<int>());
                        }
                    }
                }

                // record data
                var m_sparseEntries = new Dictionary<int, SparseEntry>(storage.Count);
                foreach (var record in serializer.Records)
                {
                    if (!m_copyData.TryGetValue(record.Key, out int parent))
                    {
                        m_sparseEntries.Add(record.Key, new SparseEntry()
                        {
                            Offset = (uint)writer.BaseStream.Position,
                            Size = (ushort)record.Value.TotalBytesWrittenOut
                        });

                        record.Value.CopyTo(writer.BaseStream);
                    }
                }

                // string table
                if (!Flags.HasFlagExt(DB2Flags.Sparse))
                {
                    writer.WriteCString("");
                    foreach (var str in m_stringsTable)
                        writer.WriteCString(str.Key);
                }

                // set the OffsetRecordsEndOffset
                if (Flags.HasFlagExt(DB2Flags.Sparse))
                {
                    long oldPos = writer.BaseStream.Position;
                    writer.BaseStream.Position = 92;
                    writer.Write((uint)oldPos);
                    writer.BaseStream.Position = oldPos;
                }

                // index table
                if (Flags.HasFlagExt(DB2Flags.Index))
                    writer.WriteArray(serializer.Records.Keys.Except(m_copyData.Keys).ToArray());

                // copy table
                foreach (var copyRecord in m_copyData)
                {
                    writer.Write(copyRecord.Key);
                    writer.Write(copyRecord.Value);
                }

                // sparse data
                if (Flags.HasFlagExt(DB2Flags.Sparse))
                    writer.WriteArray(m_sparseEntries.Values.ToArray());

                // reference data
                if (m_referenceData.Count > 0)
                {
                    writer.Write(m_referenceData.Count);
                    writer.Write(m_referenceData.Min());
                    writer.Write(m_referenceData.Max());

                    for (int i = 0; i < m_referenceData.Count; i++)
                    {
                        writer.Write(m_referenceData[i]);
                        writer.Write(i);
                    }
                }

                // sparse data idss
                if (Flags.HasFlagExt(DB2Flags.Sparse))
                    writer.WriteArray(m_sparseEntries.Keys.ToArray());
            }
        }

        private (int CommonDataSize, int PalletDataSize, int RefDataSize) GetDataSizes()
        {
            // uint NumRecords, uint minId, uint maxId, {uint id, uint index}[NumRecords]
            int refSize = 0;
            if (m_referenceData.Count > 0)
                refSize = 12 + (m_referenceData.Count * 8);

            int commonSize = 0, palletSize = 0;
            for (int i = 0; i < m_columnMeta.Length; i++)
            {
                switch (m_columnMeta[i].CompressionType)
                {
                    // {uint id, uint copyid}[]
                    case CompressionType.Common:
                        m_columnMeta[i].AdditionalDataSize = (uint)(m_commonData[i].Count * 8);
                        commonSize += (int)m_columnMeta[i].AdditionalDataSize;
                        break;

                    // {uint values[Cardinality]}[]
                    case CompressionType.Pallet:
                    case CompressionType.PalletArray:
                        m_columnMeta[i].AdditionalDataSize = (uint)m_palletData[i].Sum(x => x.Length * 4);
                        palletSize += (int)m_columnMeta[i].AdditionalDataSize;
                        break;
                }
            }

            return (commonSize, palletSize, refSize);
        }
    }
}