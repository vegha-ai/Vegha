namespace Vegha.App.ViewModels;

/// <summary>A linked request (Chain tab) the user asked to open, with its folder chain for
/// inheritance.</summary>
public sealed class LinkedRequestOpenEventArgs(Vegha.Core.Domain.RequestItem request, IReadOnlyList<Vegha.Core.Domain.Folder> folderChain) : EventArgs
{
    public Vegha.Core.Domain.RequestItem Request { get; } = request;
    public IReadOnlyList<Vegha.Core.Domain.Folder> FolderChain { get; } = folderChain;
}
