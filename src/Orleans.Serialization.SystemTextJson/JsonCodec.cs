using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

/// <summary>
/// A serialization codec which uses <see cref="JsonSerializer"/>.
/// </summary>
[WellKnownAlias(WellKnownAlias)]
public class JsonCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    private static readonly Type SelfType = typeof(JsonCodec);
    private readonly ICodecSelector[] _serializableTypeSelectors;
    private readonly ICopierSelector[] _copyableTypeSelectors;
    private readonly JsonCodecOptions _options;

    /// <summary>
    /// The well-known type alias for this codec.
    /// </summary>
    public const string WellKnownAlias = "json";

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonCodec"/> class.
    /// </summary>
    /// <param name="serializableTypeSelectors">Filters used to indicate which types should be serialized by this codec.</param>
    /// <param name="copyableTypeSelectors">Filters used to indicate which types should be copied by this codec.</param>
    /// <param name="options">The JSON codec options.</param>
    public JsonCodec(
        IEnumerable<ICodecSelector> serializableTypeSelectors,
        IEnumerable<ICopierSelector> copyableTypeSelectors,
        IOptions<JsonCodecOptions> options)
    {
        _serializableTypeSelectors = serializableTypeSelectors.Where(t => string.Equals(t.CodecName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _copyableTypeSelectors = copyableTypeSelectors.Where(t => string.Equals(t.CopierName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _options = options.Value;
    }

    /// <inheritdoc/>
    void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        // The schema type when serializing the field is the type of the codec.
        // In practice it could be any unique type as long as this codec is registered as the handler.
        // By checking against the codec type in IsSupportedType, the codec could also just be registered as an IGenericCodec.
        // Note that the codec is responsible for serializing the type of the value itself.
        writer.WriteFieldHeader(fieldIdDelta, expectedType, SelfType, WireType.TagDelimited);

        var type = value.GetType();

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(0, typeof(byte[]), typeof(byte[]), WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, type);

        // Write the serialized payload
        // Note that the Utf8JsonWriter and PooledArrayBufferWriter could be pooled as long as they're correctly
        // reset at the end of each use.
        var bufferWriter = new BufferWriterBox<PooledArrayBufferWriter>(new PooledArrayBufferWriter());
        try
        {
            using var jsonWriter = new Utf8JsonWriter(bufferWriter);
            JsonSerializer.Serialize(jsonWriter, value, _options.SerializerOptions);
            jsonWriter.Flush();

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(1, typeof(byte[]), typeof(byte[]), WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)bufferWriter.Value.Length);
            bufferWriter.Value.CopyTo(ref writer);
        }
        finally
        {
            bufferWriter.Value.Dispose();
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);
        }

        if (field.WireType != WireType.TagDelimited)
        {
            ThrowUnsupportedWireTypeException(field);
        }

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        object result = null;
        Type type = null;
        uint fieldId = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            fieldId += header.FieldIdDelta;
            switch (fieldId)
            {
                case 0:
                    ReferenceCodec.MarkValueField(reader.Session);
                    type = reader.Session.TypeCodec.ReadLengthPrefixed(ref reader);
                    break;
                case 1:
                    if (type is null)
                    {
                        ThrowTypeFieldMissing();
                    }

                    ReferenceCodec.MarkValueField(reader.Session);
                    var length = reader.ReadVarUInt32();

                    // To possibly improve efficiency, this could be converted to read a ReadOnlySequence<byte> instead of a byte array.
                    var tempBuffer = new PooledArrayBufferWriter();
                    try
                    {
                        reader.ReadBytes(ref tempBuffer, (int)length);
                        var sequence = tempBuffer.AsReadOnlySequence();
                        var jsonReader = new Utf8JsonReader(sequence, _options.ReaderOptions);
                        result = JsonSerializer.Deserialize(ref jsonReader, type, _options.SerializerOptions);
                    }
                    finally
                    {
                        tempBuffer.Dispose();
                    }

                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        return result;
    }

    /// <inheritdoc/>
    bool IGeneralizedCodec.IsSupportedType(Type type)
    {
        if (type == SelfType)
        {
            return true;
        }

        foreach (var selector in _serializableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
            {
                return true;
            }
        }

        if (_options.IsSerializableType?.Invoke(type) is bool value)
        {
            return value;
        }

        return false;
    }

    /// <inheritdoc/>
    object IDeepCopier<object>.DeepCopy(object input, CopyContext context)
    {
        if (input is null) return null;

        var bufferWriter = new BufferWriterBox<PooledArrayBufferWriter>(new PooledArrayBufferWriter());
        using var jsonWriter = new Utf8JsonWriter(bufferWriter);
        JsonSerializer.Serialize(jsonWriter, input, _options.SerializerOptions);
        var sequence = bufferWriter.Value.AsReadOnlySequence();
        try
        {
            var jsonReader = new Utf8JsonReader(sequence, _options.ReaderOptions);
            var result = JsonSerializer.Deserialize(ref jsonReader, input.GetType(), _options.SerializerOptions);
            context.RecordCopy(input, result);
            return result;
        }
        finally
        {
            bufferWriter.Value.Dispose();
        }
    }

    /// <inheritdoc/>
    bool IGeneralizedCopier.IsSupportedType(Type type)
    {
        foreach (var selector in _copyableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
            {
                return true;
            }
        }

        if (_options.IsCopyableType?.Invoke(type) is bool value)
        {
            return value;
        }

        return false;
    }

    /// <inheritdoc/>
    bool? ITypeFilter.IsTypeAllowed(Type type) => (((IGeneralizedCopier)this).IsSupportedType(type) || ((IGeneralizedCodec)this).IsSupportedType(type)) ? true : null;

    private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
        $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for JSON fields. {field}");

    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");
}