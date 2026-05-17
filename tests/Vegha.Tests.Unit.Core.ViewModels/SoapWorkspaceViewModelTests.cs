using Vegha.App.ViewModels;
using Vegha.App.ViewModels.Tabs;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class SoapWorkspaceViewModelTests
{
    // A small inline WSDL covering two operations + a SOAP 1.1 binding + an endpoint.
    private const string Wsdl = """
        <definitions name="Calculator"
                     targetNamespace="http://acme.test/calc"
                     xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:tns="http://acme.test/calc"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/">
          <portType name="CalcPort">
            <operation name="Add">
              <input message="tns:AddRequest"/>
              <output message="tns:AddResponse"/>
            </operation>
            <operation name="Subtract">
              <input message="tns:SubtractRequest"/>
              <output message="tns:SubtractResponse"/>
            </operation>
          </portType>
          <binding name="CalcBinding" type="tns:CalcPort">
            <soap:binding transport="http://schemas.xmlsoap.org/soap/http"/>
            <operation name="Add">
              <soap:operation soapAction="http://acme.test/calc/Add"/>
            </operation>
            <operation name="Subtract">
              <soap:operation soapAction="http://acme.test/calc/Subtract"/>
            </operation>
          </binding>
          <service name="Calculator">
            <port name="CalcPort" binding="tns:CalcBinding">
              <soap:address location="https://api.acme.test/calc"/>
            </port>
          </service>
        </definitions>
        """;

    private static SoapWorkspaceViewModel NewVm() =>
        new(new HttpExecutor(new System.Net.Http.HttpClient()),
            NullLogger<SoapWorkspaceViewModel>.Instance);

    [Fact]
    public async Task LoadWsdl_FromInlineXml_PopulatesOperations_AndEndpoint()
    {
        var vm = NewVm();
        vm.WsdlSource = Wsdl;

        await vm.LoadWsdlCommand.ExecuteAsync(null);

        vm.Operations.Should().HaveCount(2);
        vm.EndpointUrl.Should().Be("https://api.acme.test/calc");
        vm.SelectedOperation.Should().NotBeNull();
        vm.SelectedOperation!.Name.Should().Be("Add");
        vm.SoapAction.Should().Be("http://acme.test/calc/Add");
    }

    [Fact]
    public async Task SelectingDifferentOperation_UpdatesSoapAction()
    {
        var vm = NewVm();
        vm.WsdlSource = Wsdl;
        await vm.LoadWsdlCommand.ExecuteAsync(null);

        vm.SelectedOperation = vm.Operations.First(o => o.Name == "Subtract");
        vm.SoapAction.Should().Be("http://acme.test/calc/Subtract");
    }

    [Fact]
    public async Task SelectingOperation_SeedsBodyXml_FromMessageElementName()
    {
        var vm = NewVm();
        vm.WsdlSource = Wsdl;

        await vm.LoadWsdlCommand.ExecuteAsync(null);

        // First operation is auto-selected; its input message is "tns:AddRequest"
        // → seed body uses local name "AddRequest".
        vm.BodyXml.Should().Contain("<AddRequest");
    }

    [Fact]
    public void SoapTab_HasSoapKind_AndSoapMethodLabel()
    {
        var ws = NewVm();
        var req = new RequestItem { Name = "calc", Kind = RequestKind.Soap };
        var tab = new SoapRequestTabViewModel(ws, req, sourcePath: null, id: "draft:1");

        tab.Kind.Should().Be(RequestKind.Soap);
        tab.Method.Should().Be("SOAP");
        tab.Workspace.Should().BeSameAs(ws);
    }
}
