using Vegha.Core.Bru.Parser;
using Vegha.Core.Domain;
using Vegha.Core.Importers;
using FluentAssertions;
using Xunit;

namespace Vegha.Tests.Unit.Core.Importers;

/// <summary>Round-trips the <c>soap { }</c> block (SOAP WS-Security / WS-Addressing config)
/// through <see cref="BruEmitter"/> → <see cref="BruParser"/> → <see cref="BruToRequestConverter"/>.</summary>
public class SoapConfigBruTests
{
    private static RequestItem RoundTrip(RequestItem item) =>
        BruToRequestConverter.Convert(BruParser.Parse(BruEmitter.Emit(item)));

    private static RequestItem SoapRequest(SoapConfig? soap) => new()
    {
        Name = "GetWeather",
        Method = "POST",
        Url = "http://acme.test/weather",
        MetaType = "soap",
        Body = new BodyConfig { Mode = BodyMode.Xml, Content = "<soapenv:Envelope/>" },
        Soap = soap,
    };

    [Fact]
    public void NullSoap_EmitsNoBlock()
    {
        var bru = BruEmitter.Emit(SoapRequest(null));
        bru.Should().NotContain("soap {");
        RoundTrip(SoapRequest(null)).Soap.Should().BeNull();
    }

    [Fact]
    public void Timestamp_RoundTrips()
    {
        var rt = RoundTrip(SoapRequest(new SoapConfig
        {
            Timestamp = new WssTimestampConfig { TimeToLiveSeconds = 300 },
        }));

        rt.Soap.Should().NotBeNull();
        rt.Soap!.Timestamp.Should().NotBeNull();
        rt.Soap.Timestamp!.TimeToLiveSeconds.Should().Be(300);
        rt.Soap.UsernameToken.Should().BeNull();
        rt.Soap.Addressing.Should().BeNull();
    }

    [Fact]
    public void UsernameToken_RoundTrips()
    {
        var rt = RoundTrip(SoapRequest(new SoapConfig
        {
            UsernameToken = new WssUsernameTokenConfig
            {
                Username = "{{wssUser}}",
                Password = "s3cret",
                PasswordType = WssPasswordType.Digest,
                AddNonce = true,
                AddCreated = false,
            },
        }));

        var ut = rt.Soap!.UsernameToken;
        ut.Should().NotBeNull();
        ut!.Username.Should().Be("{{wssUser}}");
        ut.Password.Should().Be("s3cret");
        ut.PasswordType.Should().Be(WssPasswordType.Digest);
        ut.AddNonce.Should().BeTrue();
        ut.AddCreated.Should().BeFalse();
    }

    [Fact]
    public void Addressing_RoundTrips()
    {
        var rt = RoundTrip(SoapRequest(new SoapConfig
        {
            Addressing = new WsAddressingConfig
            {
                Action = "urn:GetWeather",
                To = "http://acme.test/weather",
                AutoMessageId = true,
            },
        }));

        var wsa = rt.Soap!.Addressing;
        wsa.Should().NotBeNull();
        wsa!.Action.Should().Be("urn:GetWeather");
        wsa.To.Should().Be("http://acme.test/weather");
        wsa.ReplyTo.Should().BeNull();
        wsa.AutoMessageId.Should().BeTrue();
    }

    [Fact]
    public void AllSections_RoundTripTogether()
    {
        var soap = new SoapConfig
        {
            Timestamp = new WssTimestampConfig { TimeToLiveSeconds = 120 },
            UsernameToken = new WssUsernameTokenConfig { Username = "u", Password = "p" },
            Addressing = new WsAddressingConfig { Action = "urn:Op", AutoMessageId = false },
        };

        var rt = RoundTrip(SoapRequest(soap));
        rt.Soap.Should().Be(soap);
    }
}
