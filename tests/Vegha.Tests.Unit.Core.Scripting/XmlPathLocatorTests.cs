using FluentAssertions;
using Vegha.Core.Scripting.Testing;
using Xunit;

namespace Vegha.Tests.Unit.Core.Scripting;

public class XmlPathLocatorTests
{
    private const string Soap = """
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <GetUserResponse xmlns="urn:users">
              <Token>abc123</Token>
              <Roles>
                <Role>admin</Role>
                <Role>dev</Role>
              </Roles>
              <Empty />
            </GetUserResponse>
          </soap:Body>
        </soap:Envelope>
        """;

    private const string Plain = """
        <order>
          <id>42</id>
          <line><sku>A-1</sku></line>
          <line><sku>B-2</sku></line>
        </order>
        """;

    private static XmlPathLocator.Location At(string xml, string needle, int delta = 1)
    {
        var i = xml.IndexOf(needle, StringComparison.Ordinal);
        i.Should().BeGreaterThanOrEqualTo(0, needle);
        return XmlPathLocator.Locate(xml, i + delta)!;
    }

    private static string Resolve(string xml, string xpath) =>
        ResponseValueResolver.Resolve(ResponseExpression.Create(ResponseSource.XPath, xpath),
            new ResponseSnapshot(200, "OK", xml, 1, null)).Value.ToVariableString();

    [Fact]
    public void Namespaced_elements_use_local_name_steps_and_resolve_back()
    {
        var loc = At(Soap, "abc123");
        loc.XPath.Should().Be("/*[local-name()='Envelope']/*[local-name()='Body']/*[local-name()='GetUserResponse']/*[local-name()='Token']");
        loc.SuggestedName.Should().Be("Token");
        Resolve(Soap, loc.XPath).Should().Be("abc123");
    }

    [Fact]
    public void Repeated_siblings_get_positions()
    {
        var loc = At(Soap, "dev");
        loc.XPath.Should().EndWith("*[local-name()='Role'][2]");
        Resolve(Soap, loc.XPath).Should().Be("dev");

        var sku = At(Plain, "B-2");
        sku.XPath.Should().Be("/order/line[2]/sku");
        Resolve(Plain, sku.XPath).Should().Be("B-2");
    }

    [Fact]
    public void Plain_and_empty_elements()
    {
        At(Plain, "42").XPath.Should().Be("/order/id");
        At(Soap, "<Empty", delta: 2).XPath.Should().EndWith("*[local-name()='Empty']");
    }

    [Fact]
    public void Located_xpath_round_trips_through_storage_readably()
    {
        var xpath = At(Soap, "abc123").XPath;
        var stored = ResponseExpression.Create(ResponseSource.XPath, xpath).ToStorage();
        stored.Should().Be($"xpath(\"{xpath}\")", "single-quoted predicates switch to double quotes");
        ResponseExpression.Parse(stored).Path.Should().Be(xpath);
    }

    [Fact]
    public void Invalid_input_is_tolerated()
    {
        XmlPathLocator.Locate("", 0).Should().BeNull();
        XmlPathLocator.Locate("not xml at all", 3).Should().BeNull();
    }
}
