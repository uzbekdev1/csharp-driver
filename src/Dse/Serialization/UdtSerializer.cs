//
//  Copyright (C) 2017 DataStax, Inc.
//
//  Please see the license for details:
//  http://www.datastax.com/terms/datastax-dse-driver-license-terms
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Dse.Serialization
{
    /// <summary>
    /// Represents a <see cref="TypeSerializer{T}"/> instance that handles UDT serialization and deserialization.
    /// </summary>
    public class UdtSerializer : TypeSerializer<object>
    {
        private readonly ConcurrentDictionary<string, UdtMap> _udtMapsByName = new ConcurrentDictionary<string, UdtMap>();
        private readonly ConcurrentDictionary<Type, UdtMap> _udtMapsByClrType = new ConcurrentDictionary<Type, UdtMap>();

        public override ColumnTypeCode CqlType
        {
            get { return ColumnTypeCode.Udt; }
        }

        protected internal UdtSerializer()
        {

        }

        protected internal virtual Type GetClrType(IColumnInfo typeInfo)
        {
            var udtInfo = (UdtColumnInfo)typeInfo;
            var map = GetUdtMap(udtInfo.Name);
            return map == null ? typeof(byte[]) : map.NetType;
        }

        protected internal virtual UdtMap GetUdtMap(string name)
        {
            UdtMap map;
            _udtMapsByName.TryGetValue(name, out map);
            return map;
        }

        protected internal virtual UdtMap GetUdtMap(Type type)
        {
            UdtMap map;
            _udtMapsByClrType.TryGetValue(type, out map);
            return map;
        }

        public override object Deserialize(ushort protocolVersion, byte[] buffer, int offset, int length, IColumnInfo typeInfo)
        {
            var udtInfo = (UdtColumnInfo)typeInfo;
            var map = GetUdtMap(udtInfo.Name);
            if (map == null)
            {
                return buffer;
            }
            var valuesList = new object[udtInfo.Fields.Count];
            var maxOffset = offset + length;
            for (var i = 0; i < udtInfo.Fields.Count; i++)
            {
                var field = udtInfo.Fields[i];
                if (offset >= maxOffset)
                {
                    break;
                }
                var itemLength = BeConverter.ToInt32(buffer, offset);
                offset += 4;
                if (itemLength < 0)
                {
                    continue;
                }
                valuesList[i] = DeserializeChild(buffer, offset, itemLength, field.TypeCode, field.TypeInfo);
                offset += itemLength;
            }
            return map.ToObject(valuesList);
        }

        public override byte[] Serialize(ushort protocolVersion, object value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            var map = GetUdtMap(value.GetType());
            if (map == null)
            {
                return null;
            }
            var bufferList = new List<byte[]>();
            var bufferLength = 0;
            foreach (var field in map.Definition.Fields)
            {
                object fieldValue = null;
                var prop = map.GetPropertyForUdtField(field.Name);
                if (prop != null)
                {
                    fieldValue = prop.GetValue(value, null);
                }
                var itemBuffer = SerializeChild(fieldValue);
                bufferList.Add(itemBuffer);
                if (fieldValue != null)
                {
                    bufferLength += itemBuffer.Length;
                }
            }
            return EncodeBufferList(bufferList, bufferLength);
        }

        /// <summary>
        /// Sets a Udt map for a given Udt name
        /// </summary>
        /// <param name="name">Fully qualified udt name case sensitive (keyspace.udtName)</param>
        /// <param name="map"></param>
        public virtual void SetUdtMap(string name, UdtMap map)
        {
            _udtMapsByName.AddOrUpdate(name, map, (k, oldValue) => map);
            _udtMapsByClrType.AddOrUpdate(map.NetType, map, (k, oldValue) => map);
        }
    }
}
