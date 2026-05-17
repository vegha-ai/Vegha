extern alias GoogleProto;

using Vegha.Integrations.Grpc;
using FluentAssertions;
using GoogleProto::Google.Protobuf;
using GoogleProto::Google.Protobuf.Reflection;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Round-trip tests for the dynamic protobuf JSON ↔ wire codec.
/// We hand-build a FileDescriptorProto in code, feed it to <see cref="GrpcDescriptorIndex"/>,
/// then verify EncodeJsonToWire + DecodeWireToJson agree both directions for the
/// scalar / message / repeated combinations the workspace supports.</summary>
public class GrpcDescriptorIndexTests
{
    private static byte[] BuildHelloDescriptor()
    {
        var inner = new DescriptorProto { Name = "Inner" };
        inner.Field.Add(new FieldDescriptorProto
        {
            Name = "note",
            Number = 1,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.String,
            JsonName = "note",
        });

        var hello = new DescriptorProto { Name = "Hello" };
        hello.Field.Add(MakeScalar("name", 1, FieldDescriptorProto.Types.Type.String));
        hello.Field.Add(MakeScalar("count", 2, FieldDescriptorProto.Types.Type.Int32));
        hello.Field.Add(MakeScalar("active", 3, FieldDescriptorProto.Types.Type.Bool));
        hello.Field.Add(new FieldDescriptorProto
        {
            Name = "inner",
            Number = 4,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Message,
            TypeName = ".test.Inner",
            JsonName = "inner",
        });
        hello.Field.Add(new FieldDescriptorProto
        {
            Name = "tags",
            Number = 5,
            Label = FieldDescriptorProto.Types.Label.Repeated,
            Type = FieldDescriptorProto.Types.Type.String,
            JsonName = "tags",
        });

        var file = new FileDescriptorProto
        {
            Name = "hello.proto",
            Package = "test",
            Syntax = "proto3",
        };
        file.MessageType.Add(inner);
        file.MessageType.Add(hello);
        return file.ToByteArray();
    }

    private static FieldDescriptorProto MakeScalar(
        string name, int number, FieldDescriptorProto.Types.Type type) =>
        new() {
            Name = name,
            Number = number,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = type,
            JsonName = name,
        };

    private static GrpcDescriptorIndex BuildIndex()
        => GrpcDescriptorIndex.FromFileDescriptorProtos(new[] { BuildHelloDescriptor() });

    [Fact]
    public void Index_ExposesMessages_ByFullName()
    {
        var index = BuildIndex();
        index.Messages.Keys.Should().Contain(new[] { "test.Hello", "test.Inner" });
    }

    [Fact]
    public void CreateJsonSkeleton_EmitsDefaultsForEachField()
    {
        var index = BuildIndex();
        var json = index.CreateJsonSkeleton("test.Hello");

        json.Should().Contain("\"name\": \"\"");
        json.Should().Contain("\"count\": 0");
        json.Should().Contain("\"active\": false");
        json.Should().Contain("\"inner\":");
        json.Should().Contain("\"tags\": []");
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsScalarsAndNestedMessage()
    {
        var index = BuildIndex();
        const string input = """
            {
              "name": "alice",
              "count": 7,
              "active": true,
              "inner": { "note": "hi" },
              "tags": ["a", "b", "c"]
            }
            """;

        var bytes = index.EncodeJsonToWire("test.Hello", input);
        bytes.Should().NotBeEmpty();

        var decoded = index.DecodeWireToJson("test.Hello", bytes);
        decoded.Should().Contain("\"name\": \"alice\"");
        decoded.Should().Contain("\"count\": 7");
        decoded.Should().Contain("\"active\": true");
        decoded.Should().Contain("\"note\": \"hi\"");
        decoded.Should().Contain("\"tags\": [\"a\", \"b\", \"c\"]");
    }

    [Fact]
    public void Encode_UnknownField_IsSilentlySkipped()
    {
        var index = BuildIndex();
        var bytes = index.EncodeJsonToWire("test.Hello", """{"name":"x","extra":42}""");
        var decoded = index.DecodeWireToJson("test.Hello", bytes);
        decoded.Should().Contain("\"name\": \"x\"");
        decoded.Should().NotContain("extra");
    }

    [Fact]
    public void Encode_StringEscape_RoundTrips()
    {
        var index = BuildIndex();
        var bytes = index.EncodeJsonToWire("test.Hello", """{"name":"a\"b\nc"}""");
        var decoded = index.DecodeWireToJson("test.Hello", bytes);
        decoded.Should().Contain("\"name\": \"a\\\"b\\nc\"");
    }
}
