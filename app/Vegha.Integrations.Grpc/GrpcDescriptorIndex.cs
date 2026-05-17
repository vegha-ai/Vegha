using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Vegha.Integrations.Grpc;

/// <summary>
/// Descriptor index built from one or more FileDescriptorProtos returned by gRPC
/// reflection. Exposes the parsed services + methods so the UI can list them, plus
/// JSON ↔ wire-bytes codecs that drive the message editor.
///
/// Encoding/decoding is dynamic — it walks the <see cref="MessageDescriptor"/> at
/// runtime instead of relying on generated message types — so the workspace can
/// talk to any service the reflection endpoint exposes.
/// </summary>
public sealed class GrpcDescriptorIndex
{
    private readonly Dictionary<string, MessageDescriptor> _messagesByFullName =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceDescriptor> _servicesByFullName =
        new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ServiceDescriptor> Services => _servicesByFullName;
    public IReadOnlyDictionary<string, MessageDescriptor> Messages => _messagesByFullName;

    /// <summary>Builds an index from the raw FileDescriptorProto byte arrays returned by
    /// <see cref="GrpcReflectionClient.GetFileContainingSymbolAsync"/>.</summary>
    public static GrpcDescriptorIndex FromFileDescriptorProtos(IEnumerable<byte[]> protoBytes)
    {
        var byteStrings = protoBytes.Select(ByteString.CopyFrom).ToArray();
        var fileDescriptors = FileDescriptor.BuildFromByteStrings(byteStrings);

        var index = new GrpcDescriptorIndex();
        foreach (var fd in fileDescriptors)
        {
            foreach (var svc in fd.Services)
                index._servicesByFullName[svc.FullName] = svc;
            foreach (var msg in fd.MessageTypes)
                index.IndexMessage(msg);
        }
        return index;
    }

    private void IndexMessage(MessageDescriptor msg)
    {
        _messagesByFullName[msg.FullName] = msg;
        foreach (var nested in msg.NestedTypes) IndexMessage(nested);
    }

    /// <summary>Generates a JSON skeleton for <paramref name="messageFullName"/> with default
    /// values for each scalar and an empty object/array for nested fields. The user fills in
    /// actual values before sending.</summary>
    public string CreateJsonSkeleton(string messageFullName)
    {
        if (!_messagesByFullName.TryGetValue(messageFullName, out var msg))
            throw new KeyNotFoundException($"Message not found: {messageFullName}");
        var sb = new System.Text.StringBuilder();
        WriteSkeleton(msg, sb, depth: 0, visited: new HashSet<string>(StringComparer.Ordinal));
        return sb.ToString();
    }

    private static void WriteSkeleton(MessageDescriptor msg, System.Text.StringBuilder sb, int depth, HashSet<string> visited)
    {
        // Cycle guard: if this message type is already on the recursion stack, emit an
        // empty object so we don't infinitely expand self-referential types.
        if (!visited.Add(msg.FullName))
        {
            sb.Append("{}");
            return;
        }
        try
        {
            sb.AppendLine("{");
            var pad = new string(' ', (depth + 1) * 2);
            var endPad = new string(' ', depth * 2);
            var fields = msg.Fields.InFieldNumberOrder();
            for (var i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                sb.Append(pad).Append('"').Append(f.JsonName).Append("\": ");
                if (f.IsRepeated) sb.Append("[]");
                else WriteScalarSkeleton(f, sb, depth + 1, visited);
                if (i < fields.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append(endPad).Append('}');
        }
        finally { visited.Remove(msg.FullName); }
    }

    private static void WriteScalarSkeleton(FieldDescriptor f, System.Text.StringBuilder sb, int depth, HashSet<string> visited)
    {
        switch (f.FieldType)
        {
            case FieldType.String:
            case FieldType.Bytes: sb.Append("\"\""); break;
            case FieldType.Bool: sb.Append("false"); break;
            case FieldType.Double:
            case FieldType.Float: sb.Append("0.0"); break;
            case FieldType.Enum: sb.Append("\"").Append(f.EnumType.Values[0].Name).Append("\""); break;
            case FieldType.Message: WriteSkeleton(f.MessageType, sb, depth, visited); break;
            default: sb.Append("0"); break;  // ints
        }
    }

    /// <summary>Encodes a JSON object to wire-format protobuf bytes for the named message.
    /// Handles scalars, singular nested messages, and repeated fields. Maps and groups
    /// throw <see cref="NotSupportedException"/>.</summary>
    public byte[] EncodeJsonToWire(string messageFullName, string json)
    {
        if (!_messagesByFullName.TryGetValue(messageFullName, out var msg))
            throw new KeyNotFoundException($"Message not found: {messageFullName}");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        EncodeMessage(msg, doc.RootElement, output);
        output.Flush();
        return ms.ToArray();
    }

    /// <summary>Decodes wire-format protobuf bytes to a JSON string for the named message.</summary>
    public string DecodeWireToJson(string messageFullName, byte[] bytes)
    {
        if (!_messagesByFullName.TryGetValue(messageFullName, out var msg))
            throw new KeyNotFoundException($"Message not found: {messageFullName}");
        var input = new CodedInputStream(bytes);
        var sb = new System.Text.StringBuilder();
        DecodeMessage(msg, input, sb, depth: 0);
        return sb.ToString();
    }

    // ---- Encoding ----

    private static void EncodeMessage(MessageDescriptor msg, System.Text.Json.JsonElement obj, CodedOutputStream output)
    {
        if (obj.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var prop in obj.EnumerateObject())
        {
            var f = msg.FindFieldByName(prop.Name) ?? FindByJsonName(msg, prop.Name);
            if (f is null) continue;  // unknown field — skip (forward compat)
            EncodeField(f, prop.Value, output);
        }
    }

    private static FieldDescriptor? FindByJsonName(MessageDescriptor msg, string jsonName)
    {
        foreach (var f in msg.Fields.InDeclarationOrder())
            if (string.Equals(f.JsonName, jsonName, StringComparison.Ordinal)) return f;
        return null;
    }

    private static void EncodeField(FieldDescriptor f, System.Text.Json.JsonElement value, CodedOutputStream output)
    {
        if (f.IsMap) throw new NotSupportedException("Map fields are not yet supported.");
        if (f.IsRepeated)
        {
            if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            foreach (var item in value.EnumerateArray()) EncodeSingular(f, item, output);
            return;
        }
        EncodeSingular(f, value, output);
    }

    private static void EncodeSingular(FieldDescriptor f, System.Text.Json.JsonElement v, CodedOutputStream output)
    {
        switch (f.FieldType)
        {
            case FieldType.String:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteString(v.GetString() ?? string.Empty); break;
            case FieldType.Bytes:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? Convert.FromBase64String(v.GetString() ?? string.Empty) : Array.Empty<byte>())); break;
            case FieldType.Bool:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.Varint);
                output.WriteBool(v.GetBoolean()); break;
            case FieldType.Int32: case FieldType.SInt32: case FieldType.SFixed32:
                output.WriteTag(f.FieldNumber, WireType(f.FieldType));
                if (f.FieldType == FieldType.SFixed32) output.WriteSFixed32(v.GetInt32());
                else if (f.FieldType == FieldType.SInt32) output.WriteSInt32(v.GetInt32());
                else output.WriteInt32(v.GetInt32()); break;
            case FieldType.UInt32: case FieldType.Fixed32:
                output.WriteTag(f.FieldNumber, WireType(f.FieldType));
                if (f.FieldType == FieldType.Fixed32) output.WriteFixed32(v.GetUInt32());
                else output.WriteUInt32(v.GetUInt32()); break;
            case FieldType.Int64: case FieldType.SInt64: case FieldType.SFixed64:
                output.WriteTag(f.FieldNumber, WireType(f.FieldType));
                if (f.FieldType == FieldType.SFixed64) output.WriteSFixed64(v.GetInt64());
                else if (f.FieldType == FieldType.SInt64) output.WriteSInt64(v.GetInt64());
                else output.WriteInt64(v.GetInt64()); break;
            case FieldType.UInt64: case FieldType.Fixed64:
                output.WriteTag(f.FieldNumber, WireType(f.FieldType));
                if (f.FieldType == FieldType.Fixed64) output.WriteFixed64(v.GetUInt64());
                else output.WriteUInt64(v.GetUInt64()); break;
            case FieldType.Double:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.Fixed64);
                output.WriteDouble(v.GetDouble()); break;
            case FieldType.Float:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.Fixed32);
                output.WriteFloat(v.GetSingle()); break;
            case FieldType.Enum:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.Varint);
                output.WriteEnum(ResolveEnumNumber(f, v)); break;
            case FieldType.Message:
                output.WriteTag(f.FieldNumber, WireFormat.WireType.LengthDelimited);
                using (var inner = new MemoryStream())
                {
                    var innerOut = new CodedOutputStream(inner);
                    EncodeMessage(f.MessageType, v, innerOut);
                    innerOut.Flush();
                    output.WriteBytes(ByteString.CopyFrom(inner.ToArray()));
                }
                break;
            default:
                throw new NotSupportedException($"Field type {f.FieldType} not supported for {f.FullName}");
        }
    }

    private static WireFormat.WireType WireType(FieldType t) => t switch
    {
        FieldType.Fixed32 or FieldType.SFixed32 or FieldType.Float => WireFormat.WireType.Fixed32,
        FieldType.Fixed64 or FieldType.SFixed64 or FieldType.Double => WireFormat.WireType.Fixed64,
        _ => WireFormat.WireType.Varint,
    };

    private static int ResolveEnumNumber(FieldDescriptor f, System.Text.Json.JsonElement v)
    {
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number) return v.GetInt32();
        var name = v.GetString();
        var enumValue = f.EnumType.FindValueByName(name) ?? f.EnumType.Values[0];
        return enumValue.Number;
    }

    // ---- Decoding ----

    private static void DecodeMessage(MessageDescriptor msg, CodedInputStream input, System.Text.StringBuilder sb, int depth)
    {
        sb.Append('{');
        var pad = new string(' ', (depth + 1) * 2);
        var endPad = new string(' ', depth * 2);
        sb.AppendLine();

        // Repeated fields collect into per-field-number buckets, then emit as JSON arrays.
        var collected = new Dictionary<int, List<string>>();
        var fieldOrder = new List<int>();

        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            if (tag == 0) break;
            var fieldNum = WireFormat.GetTagFieldNumber(tag);
            var wire = WireFormat.GetTagWireType(tag);
            var f = msg.FindFieldByNumber(fieldNum);
            if (f is null)
            {
                input.SkipLastField();
                continue;
            }
            var rendered = ReadField(f, wire, input, depth + 1);
            if (!collected.TryGetValue(fieldNum, out var bucket))
            {
                bucket = new List<string>();
                collected[fieldNum] = bucket;
                fieldOrder.Add(fieldNum);
            }
            bucket.Add(rendered);
        }

        for (var i = 0; i < fieldOrder.Count; i++)
        {
            var fn = fieldOrder[i];
            var f = msg.FindFieldByNumber(fn)!;
            var bucket = collected[fn];
            sb.Append(pad).Append('"').Append(f.JsonName).Append("\": ");
            if (f.IsRepeated)
            {
                sb.Append('[').Append(string.Join(", ", bucket)).Append(']');
            }
            else
            {
                sb.Append(bucket[bucket.Count - 1]);  // proto3 semantics: last-wins
            }
            if (i < fieldOrder.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.Append(endPad).Append('}');
    }

    private static string ReadField(FieldDescriptor f, WireFormat.WireType wire, CodedInputStream input, int depth)
    {
        return f.FieldType switch
        {
            FieldType.String => "\"" + JsonEscape(input.ReadString()) + "\"",
            FieldType.Bytes => "\"" + Convert.ToBase64String(input.ReadBytes().ToByteArray()) + "\"",
            FieldType.Bool => input.ReadBool() ? "true" : "false",
            FieldType.Int32 => input.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.SInt32 => input.ReadSInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.SFixed32 => input.ReadSFixed32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.UInt32 => input.ReadUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Fixed32 => input.ReadFixed32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Int64 => input.ReadInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.SInt64 => input.ReadSInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.SFixed64 => input.ReadSFixed64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.UInt64 => input.ReadUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Fixed64 => input.ReadFixed64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Double => input.ReadDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Float => input.ReadFloat().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Enum => "\"" + (f.EnumType.FindValueByNumber(input.ReadEnum())?.Name ?? "UNKNOWN") + "\"",
            FieldType.Message => ReadEmbeddedMessage(f, input, depth),
            _ => throw new NotSupportedException($"Field type {f.FieldType} not supported"),
        };
    }

    private static string ReadEmbeddedMessage(FieldDescriptor f, CodedInputStream input, int depth)
    {
        var bytes = input.ReadBytes().ToByteArray();
        var inner = new CodedInputStream(bytes);
        var sb = new System.Text.StringBuilder();
        DecodeMessage(f.MessageType, inner, sb, depth);
        return sb.ToString();
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
