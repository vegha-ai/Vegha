using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Integration;

/// <summary>Verifies <see cref="SoapSecurityProcessor"/> injects WS-Security / WS-Addressing
/// headers into an outgoing SOAP envelope correctly.</summary>
public class SoapSecurityProcessorTests
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private static readonly XNamespace Wsa = "http://www.w3.org/2005/08/addressing";

    private const string Envelope =
        "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
        "<soapenv:Body><GetWeather><City>Seattle</City></GetWeather></soapenv:Body>" +
        "</soapenv:Envelope>";

    private static XElement Parse(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void NoConfig_ReturnsInputUnchanged()
    {
        SoapSecurityProcessor.Apply(Envelope, null).Should().Be(Envelope);
        SoapSecurityProcessor.Apply(Envelope, new SoapConfig()).Should().Be(Envelope);
    }

    [Fact]
    public void NonXmlBody_ReturnsInputUnchanged()
    {
        const string notXml = "this is not xml";
        var cfg = new SoapConfig { Timestamp = new WssTimestampConfig() };
        SoapSecurityProcessor.Apply(notXml, cfg).Should().Be(notXml);
    }

    [Fact]
    public void Timestamp_InjectsCreatedAndExpires_WithConfiguredTtl()
    {
        var cfg = new SoapConfig { Timestamp = new WssTimestampConfig { TimeToLiveSeconds = 300 } };
        var result = Parse(SoapSecurityProcessor.Apply(Envelope, cfg));

        var timestamp = result.Element(Soap + "Header")!
            .Element(Wsse + "Security")!
            .Element(Wsu + "Timestamp")!;

        var created = DateTime.Parse(timestamp.Element(Wsu + "Created")!.Value,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var expires = DateTime.Parse(timestamp.Element(Wsu + "Expires")!.Value,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        (expires - created).TotalSeconds.Should().BeApproximately(300, 1);
    }

    [Fact]
    public void HeaderIsInsertedBeforeBody()
    {
        var cfg = new SoapConfig { Timestamp = new WssTimestampConfig() };
        var result = Parse(SoapSecurityProcessor.Apply(Envelope, cfg));

        result.Elements().First().Name.Should().Be(Soap + "Header");
        result.Elements().Last().Name.Should().Be(Soap + "Body");
    }

    [Fact]
    public void UsernameToken_Text_CarriesPlaintextPassword()
    {
        var cfg = new SoapConfig
        {
            UsernameToken = new WssUsernameTokenConfig
            {
                Username = "alice",
                Password = "p@ss",
                PasswordType = WssPasswordType.Text,
            },
        };
        var result = Parse(SoapSecurityProcessor.Apply(Envelope, cfg));
        var token = result.Element(Soap + "Header")!.Element(Wsse + "Security")!
            .Element(Wsse + "UsernameToken")!;

        token.Element(Wsse + "Username")!.Value.Should().Be("alice");
        var password = token.Element(Wsse + "Password")!;
        password.Value.Should().Be("p@ss");
        password.Attribute("Type")!.Value.Should().EndWith("#PasswordText");
    }

    [Fact]
    public void UsernameToken_Digest_HashesPasswordAndAddsNonceAndCreated()
    {
        var cfg = new SoapConfig
        {
            UsernameToken = new WssUsernameTokenConfig
            {
                Username = "alice",
                Password = "p@ss",
                PasswordType = WssPasswordType.Digest,
                AddNonce = false,   // digest forces nonce + created anyway
                AddCreated = false,
            },
        };
        var result = Parse(SoapSecurityProcessor.Apply(Envelope, cfg));
        var token = result.Element(Soap + "Header")!.Element(Wsse + "Security")!
            .Element(Wsse + "UsernameToken")!;

        var password = token.Element(Wsse + "Password")!;
        password.Attribute("Type")!.Value.Should().EndWith("#PasswordDigest");
        password.Value.Should().NotBe("p@ss");
        // A digest password is a base64-encoded SHA-1 hash (20 bytes → 28 base64 chars).
        password.Value.Should().HaveLength(28);
        token.Element(Wsse + "Nonce").Should().NotBeNull();
        token.Element(Wsu + "Created").Should().NotBeNull();
    }

    [Fact]
    public void Interpolation_ResolvesPlaceholdersInTokenValues()
    {
        var cfg = new SoapConfig
        {
            UsernameToken = new WssUsernameTokenConfig
            {
                Username = "{{user}}",
                Password = "{{pwd}}",
            },
        };
        var result = Parse(SoapSecurityProcessor.Apply(Envelope, cfg,
            s => s.Replace("{{user}}", "bob").Replace("{{pwd}}", "hunter2")));
        var token = result.Element(Soap + "Header")!.Element(Wsse + "Security")!
            .Element(Wsse + "UsernameToken")!;

        token.Element(Wsse + "Username")!.Value.Should().Be("bob");
        token.Element(Wsse + "Password")!.Value.Should().Be("hunter2");
    }

    [Fact]
    public void Addressing_InjectsActionToAndAutoMessageId()
    {
        var cfg = new SoapConfig
        {
            Addressing = new WsAddressingConfig
            {
                Action = "urn:GetWeather",
                To = "http://acme.test/weather",
                AutoMessageId = true,
            },
        };
        var header = Parse(SoapSecurityProcessor.Apply(Envelope, cfg)).Element(Soap + "Header")!;

        header.Element(Wsa + "Action")!.Value.Should().Be("urn:GetWeather");
        header.Element(Wsa + "To")!.Value.Should().Be("http://acme.test/weather");
        header.Element(Wsa + "MessageID")!.Value.Should().StartWith("urn:uuid:");
    }

    [Fact]
    public void ExistingSecurityHeader_IsReused_NotDuplicated()
    {
        const string withSecurity =
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<soapenv:Header>" +
            "<wsse:Security xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\"/>" +
            "</soapenv:Header>" +
            "<soapenv:Body><GetWeather/></soapenv:Body></soapenv:Envelope>";

        var cfg = new SoapConfig { Timestamp = new WssTimestampConfig() };
        var result = Parse(SoapSecurityProcessor.Apply(withSecurity, cfg));

        result.Elements(Soap + "Header").Should().ContainSingle();
        result.Element(Soap + "Header")!.Elements(Wsse + "Security").Should().ContainSingle();
        result.Element(Soap + "Header")!.Element(Wsse + "Security")!
            .Elements(Wsu + "Timestamp").Should().ContainSingle();
    }

    [Fact]
    public void ExistingEmptyHeader_IsReused_AndSecurityAddedIntoIt()
    {
        // SoapUI sample envelopes ship with an empty <soapenv:Header></soapenv:Header> —
        // the processor must add Security into that existing header, not skip it.
        const string emptyHeader =
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<soapenv:Header></soapenv:Header>" +
            "<soapenv:Body><GetWeather/></soapenv:Body></soapenv:Envelope>";

        var cfg = new SoapConfig { Timestamp = new WssTimestampConfig { TimeToLiveSeconds = 90 } };
        var result = Parse(SoapSecurityProcessor.Apply(emptyHeader, cfg));

        result.Elements(Soap + "Header").Should().ContainSingle();
        var timestamp = result.Element(Soap + "Header")!
            .Element(Wsse + "Security")!
            .Element(Wsu + "Timestamp");
        timestamp.Should().NotBeNull();
        timestamp!.Element(Wsu + "Created").Should().NotBeNull();
    }

    [Fact]
    public void Soap12Envelope_DetectedFromNamespace()
    {
        const string soap12 =
            "<env:Envelope xmlns:env=\"http://www.w3.org/2003/05/soap-envelope\">" +
            "<env:Body><GetWeather/></env:Body></env:Envelope>";
        XNamespace s12 = "http://www.w3.org/2003/05/soap-envelope";

        var cfg = new SoapConfig { Timestamp = new WssTimestampConfig() };
        var result = Parse(SoapSecurityProcessor.Apply(soap12, cfg));

        result.Element(s12 + "Header")!.Element(Wsse + "Security").Should().NotBeNull();
    }
}
