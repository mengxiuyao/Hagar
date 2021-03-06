﻿using System;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using Hagar.Buffers;
using Hagar.Codecs;
using Hagar.Session;

namespace Hagar.ISerializable
{
    /// <summary>
    /// Serializer for ISerializable reference types.
    /// </summary>
    internal class ObjectSerializer : ISerializableSerializer
    {
        private readonly SerializationCallbacksFactory serializationCallbacks;
        private readonly Func<Type, Action<object, SerializationInfo, StreamingContext>> createConstructorDelegate;

        private readonly ConcurrentDictionary<Type, Action<object, SerializationInfo, StreamingContext>> constructors =
            new ConcurrentDictionary<Type, Action<object, SerializationInfo, StreamingContext>>();

        private readonly IFormatterConverter formatterConverter;
        private readonly StreamingContext streamingContext;
        private readonly SerializationEntryCodec entrySerializer;

        public ObjectSerializer(
            SerializationEntryCodec entrySerializer,
            SerializationConstructorFactory constructorFactory,
            SerializationCallbacksFactory serializationCallbacks,
            IFormatterConverter formatterConverter,
            StreamingContext streamingContext)
        {
            this.serializationCallbacks = serializationCallbacks;
            this.formatterConverter = formatterConverter;
            this.streamingContext = streamingContext;
            this.entrySerializer = entrySerializer;
            this.createConstructorDelegate = constructorFactory.GetSerializationConstructorDelegate;
        }

        public void WriteValue(Writer writer, SerializerSession session, object value)
        {
            var type = value.GetType();
            var callbacks = this.serializationCallbacks.GetReferenceTypeCallbacks(type);
            var info = new SerializationInfo(type, formatterConverter);
            callbacks.OnSerializing?.Invoke(value, streamingContext);
            ((System.Runtime.Serialization.ISerializable) value).GetObjectData(info, streamingContext);

            var first = true;
            foreach (var field in info)
            {
                var surrogate = new SerializationEntrySurrogate(field);
                this.entrySerializer.WriteField(writer, session, first ? 1 : (uint) 0, SerializationEntryCodec.SerializationEntryType, surrogate);
                if (first) first = false;
            }
            
            callbacks.OnSerialized?.Invoke(value, streamingContext);
        }

        public object ReadValue(Reader reader, SerializerSession session, Type type, uint placeholderReferenceId)
        {
            var callbacks = this.serializationCallbacks.GetReferenceTypeCallbacks(type);

            var info = new SerializationInfo(type, formatterConverter);
            var result = FormatterServices.GetUninitializedObject(type);

            ReferenceCodec.RecordObject(session, result, placeholderReferenceId);
            callbacks.OnDeserializing?.Invoke(result, streamingContext);

            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader(session);
                if (header.IsEndBaseOrEndObject) break;
                fieldId += header.FieldIdDelta;
                if (fieldId == 1)
                {
                    var entry = this.entrySerializer.ReadValue(reader, session, header);
                    info.AddValue(entry.Name, entry.Value);
                }
            }

            var constructor = this.constructors.GetOrAdd(info.ObjectType, this.createConstructorDelegate);
            constructor(result, info, streamingContext);
            callbacks.OnDeserialized?.Invoke(result, streamingContext);
            if (result is IDeserializationCallback callback)
            {
                callback.OnDeserialization(streamingContext.Context);
            }

            return result;
        }
    }
}