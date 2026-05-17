using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Integration;

public class ImportWizardViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "vegha-import-" + Guid.NewGuid().ToString("N"));

    public ImportWizardViewModelTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private ImportWizardViewModel NewVm() =>
        new(NullLogger<ImportWizardViewModel>.Instance);

    [Fact]
    public void DetectsPostmanV21_FromInfoAndItem()
    {
        var path = Path.Combine(_tempDir, "pm.json");
        File.WriteAllText(path, """
            {
              "info": { "name": "PM Coll", "_postman_id": "x",
                        "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "Get", "request": { "method": "GET", "url": "https://x.test/y" } }
              ]
            }
            """);
        var vm = NewVm();
        vm.SelectedPath = path;
        vm.DetectedFormat.Should().StartWith("Postman v2.1");
        vm.ImportCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void DetectsPostmanEnvironment_FromValuesArrayWithoutInfo()
    {
        var path = Path.Combine(_tempDir, "pm-env.json");
        File.WriteAllText(path, """
            { "name": "Prod", "values": [{ "key": "baseUrl", "value": "https://x.test" }] }
            """);
        var vm = NewVm();
        vm.SelectedPath = path;
        vm.DetectedFormat.Should().Be("Postman environment");
    }

    [Fact]
    public void DetectsInsomniaV5_FromTypePrefix()
    {
        var path = Path.Combine(_tempDir, "insomnia5.json");
        File.WriteAllText(path, """
            { "type": "collection.insomnia.rest/5.0", "name": "I5",
              "collection": [{ "name": "X", "method": "GET", "url": "https://x.test" }] }
            """);
        var vm = NewVm();
        vm.SelectedPath = path;
        vm.DetectedFormat.Should().Be("Insomnia v5 collection");
    }

    [Fact]
    public void DetectsInsomniaV4_FromResourcesArray()
    {
        var path = Path.Combine(_tempDir, "insomnia4.json");
        File.WriteAllText(path, """
            { "_type": "export", "__export_format": 4,
              "resources": [
                { "_id": "wsp_1", "_type": "workspace", "name": "I4" },
                { "_id": "req_1", "_type": "request", "parentId": "wsp_1",
                  "name": "Ping", "method": "GET", "url": "https://x.test/p" }
              ]
            }
            """);
        var vm = NewVm();
        vm.SelectedPath = path;
        vm.DetectedFormat.Should().Be("Insomnia v4 export");
    }

    [Fact]
    public void DetectsBrunoFolder_ByPresenceOfBruFiles()
    {
        var folder = Path.Combine(_tempDir, "bruno-coll");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ping.bru"), "meta { name: ping }");

        var vm = NewVm();
        vm.SelectedPath = folder;
        vm.DetectedFormat.Should().Be("Bruno collection (folder)");
    }

    [Fact]
    public void EmptyFolder_FailsDetection_DisablesImport()
    {
        var folder = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(folder);

        var vm = NewVm();
        vm.SelectedPath = folder;
        vm.ImportCommand.CanExecute(null).Should().BeFalse();
        vm.ErrorMessage.Should().Contain(".bru");
    }

    [Fact]
    public void OnConfirmed_FiresWithStagedCollection_OrEnvironment()
    {
        var path = Path.Combine(_tempDir, "pm-env.json");
        File.WriteAllText(path, """{ "name": "E", "values": [{ "key": "k", "value": "v" }] }""");

        var vm = NewVm();
        Vegha.Core.Domain.Environment? captured = null;
        vm.OnEnvironmentConfirmed = e => captured = e;
        vm.SelectedPath = path;
        vm.ImportCommand.Execute(null);

        captured.Should().NotBeNull();
        captured!.Name.Should().Be("E");
    }

    [Fact]
    public void EnvOnlyBatch_With_AcceptEnvironmentsFalse_RejectsWithRedirect()
    {
        var path = Path.Combine(_tempDir, "pm-env.json");
        File.WriteAllText(path, """{ "name": "Prod", "values": [{ "key": "k", "value": "v" }] }""");

        var vm = NewVm();
        vm.AcceptEnvironments = false;
        vm.SelectedPath = path;

        vm.ImportCommand.CanExecute(null).Should().BeFalse();
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.ErrorMessage!.Should().Contain("environment file");
        vm.ErrorMessage.Should().Contain("Environments panel");
        vm.DetectedFormat.Should().Be("Unrecognized format");
    }

    [Fact]
    public void EnvOnlyBatch_With_AcceptEnvironmentsTrue_StillAccepts()
    {
        var path = Path.Combine(_tempDir, "pm-env.json");
        File.WriteAllText(path, """{ "name": "Prod", "values": [{ "key": "k", "value": "v" }] }""");

        var vm = NewVm();
        // AcceptEnvironments default true preserves backwards compatibility.
        vm.SelectedPath = path;

        vm.ImportCommand.CanExecute(null).Should().BeTrue();
        vm.DetectedFormat.Should().Be("Postman environment");
    }

    [Fact]
    public void EnvOnly_AcceptEnvironmentsFalse_ImportDoesNotFireEnvironmentCallback()
    {
        var path = Path.Combine(_tempDir, "pm-env.json");
        File.WriteAllText(path, """{ "name": "E", "values": [{ "key": "k", "value": "v" }] }""");

        var vm = NewVm();
        vm.AcceptEnvironments = false;
        Vegha.Core.Domain.Environment? captured = null;
        vm.OnEnvironmentConfirmed = e => captured = e;
        vm.SelectedPath = path;

        // CanExecute is false but invoke Import anyway to verify the inner guard.
        vm.ImportCommand.Execute(null);
        captured.Should().BeNull();
    }
}
