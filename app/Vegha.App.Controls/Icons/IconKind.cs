namespace Vegha.App.Controls.Icons;

/// <summary>Identifiers for the inline-SVG icon library. New kinds must be added to
/// <see cref="IconLibrary"/> at the same time.</summary>
public enum IconKind
{
    None = 0,

    // Activity rail
    Collection,
    Workspace,
    Env,
    History,
    Git,
    Vault,
    Swagger,
    OpenApi,
    Flow,
    FlowRunner,
    Team,
    Settings,
    Help,
    Cookie,

    // Common
    Search,
    Plus,
    Minus,
    Discard,
    Close,
    Menu,
    ChevronRight,
    ChevronDown,
    ChevronUp,
    More,
    MoreVertical,
    Folder,
    FolderOpen,
    FileText,
    Download,
    Upload,
    Send,
    Refresh,
    Sync,
    CloudDownload,
    CloudUpload,
    Stash,
    Branch,
    Undo,
    Filter,
    Save,
    Bell,
    Globe,
    Copy,
    Trash,
    Play,
    Stop,
    Pencil,
    Terminal,
    Info,
    Paste,
    Share,

    // Theme
    Sun,
    Moon,

    // Settings pages
    Code,
    Keyboard,

    // Form inputs
    Eye,
    Warning,

    // Drop zones
    DropFile,

    // Request kinds
    GraphQL,
}
