using System.Linq;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Vegha.App.Controls.Workspace;
using Vegha.App.ViewModels;
using Vegha.Core.Domain;
using Vegha.Core.Requests;
using Xunit;

namespace Vegha.Tests.UI;

/// <summary>
/// The auth editor is one control bound to one VM type, hosted identically by the request
/// workspace and the collection/folder settings tab. These tests inflate it against each
/// scope to prove the shared surface really does render everywhere — the request-level
/// per-type forms are not a request-only affordance.
/// </summary>
public class SharedAuthEditorTests
{
    private static AuthEditor Inflate(AuthSectionViewModel vm)
    {
        var editor = new AuthEditor { DataContext = vm };
        var window = new Window { Content = editor, Width = 900, Height = 600 };
        window.Show();
        return editor;
    }

    [AvaloniaFact]
    public void RendersAgainstACollectionScopedViewModel()
    {
        var props = new NodePropertiesViewModel(
            NodePropertiesViewModel.Kind.Collection, new Collection { Name = "Api" });
        props.Auth.AuthType = "oauth2";

        var editor = Inflate(props.Auth);

        // The OAuth2 form's Client ID field is bound and reachable — i.e. the collection tab
        // gets the same rich editor the request tab does, not a text blob.
        props.Auth.OAuth2ClientId = "cid-from-collection";
        var texts = editor.GetVisualDescendants()
            .OfType<VariableAwareTextEditor>()
            .Select(t => t.Text)
            .ToList();
        texts.Should().Contain("cid-from-collection");
    }

    [AvaloniaFact]
    public void RendersAgainstAFolderScopedViewModel()
    {
        var props = new NodePropertiesViewModel(
            NodePropertiesViewModel.Kind.Folder, new Folder { Name = "inner" });
        props.Auth.AuthType = "bearer";
        props.Auth.BearerToken = "folder-token";

        var editor = Inflate(props.Auth);

        editor.GetVisualDescendants().OfType<VariableAwareTextEditor>()
            .Select(t => t.Text).Should().Contain("folder-token");
    }

    [AvaloniaFact]
    public void RendersAgainstTheRequestEditorsSection()
    {
        var http = new HttpClient();
        var vm = new AuthSectionViewModel(AuthScope.Request, new OAuth2TokenAcquirer(http));
        vm.AuthType = "basic";
        vm.BasicUsername = "alice";

        var editor = Inflate(vm);

        editor.GetVisualDescendants().OfType<VariableAwareTextEditor>()
            .Select(t => t.Text).Should().Contain("alice");
    }

    [AvaloniaFact]
    public void TypePickerOffersInheritOnlyBelowTheCollectionLevel()
    {
        var collection = new AuthSectionViewModel(AuthScope.Collection);
        var folder = new AuthSectionViewModel(AuthScope.Folder);
        var request = new AuthSectionViewModel(AuthScope.Request);

        collection.AvailableAuthTypes.Should().NotContain("inherit");
        folder.AvailableAuthTypes.Should().Contain("inherit");
        request.AvailableAuthTypes.Should().Contain("inherit");

        // Everything else — OAuth2 included — is offered at every level.
        foreach (var scope in new[] { collection, folder, request })
        {
            scope.AvailableAuthTypes.Should().Contain(
                new[] { "oauth2", "oauth1", "awsv4", "digest", "ntlm", "wsse", "apikey", "bearer", "basic" });
        }
    }

    [AvaloniaFact]
    public void OverrideCopiesTheInheritedConfigOntoThisScope()
    {
        var vm = new AuthSectionViewModel(AuthScope.Folder)
        {
            InheritedFrom = "collection “Api”",
            InheritedAuthProvider = () => new AuthConfig
            {
                Type = AuthType.Bearer,
                Parameters = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["token"] = "from-parent",
                },
            },
        };
        vm.IsInherited.Should().BeTrue();

        vm.OverrideInheritedCommand.Execute(null);

        vm.AuthType.Should().Be("bearer");
        vm.BearerToken.Should().Be("from-parent");
    }
}
