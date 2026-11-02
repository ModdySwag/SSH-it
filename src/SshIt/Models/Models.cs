using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SshIt.Services;

namespace SshIt.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A saved SSH destination.</summary>
public sealed class Session : Observable
{
    private string _name = "";
    private string _host = "";
    private int _port = 22;
    private string _user = "";
    private string? _folderId;
    private string? _lastError;
    private bool _isOnline;
    private DateTime _lastUsed;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Display name. Falls back to user@host when empty.</summary>
    public string Name
    {
        get => string.IsNullOrWhiteSpace(_name) ? DisplayTarget : _name;
        set => Set(ref _name, value);
    }

    /// <summary>The stored name without the DisplayTarget fallback, so an empty name stays empty.</summary>
    [JsonIgnore]
    internal string StoredName => _name;

    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value)) OnPropertyChanged(nameof(DisplayTarget)); }
    }

    public int Port
    {
        get => _port;
        set => Set(ref _port, value <= 0 ? 22 : value);
    }

    public string User
    {
        get => _user;
        set { if (Set(ref _user, value)) OnPropertyChanged(nameof(DisplayTarget)); }
    }

    /// <summary>Optional: connect via an OpenSSH config Host alias instead of explicit host.</summary>
    public bool UseConfigAlias { get; set; }
    public string? ConfigAlias { get; set; }

    public string? FolderId
    {
        get => _folderId;
        set => Set(ref _folderId, value);
    }

    /// <summary>Agent | Key | Password | Interactive</summary>
    public string AuthMode { get; set; } = "Agent";
    public string? KeyPath { get; set; }
    public string? JumpHost { get; set; }

    /// <summary>
    /// Per-hop key files, keyed by the hop exactly as it is written in <see cref="JumpHost"/>
    /// ("ops@bastion:2222"). OpenSSH's -J never gives a hop the session's key, and one key for a
    /// whole chain is a real limit, so each hop may carry its own. A hop with no entry falls back to
    /// <see cref="KeyPath"/>; a hop with neither is left to the agent and the default identities.
    /// </summary>
    public Dictionary<string, string>? JumpKeys { get; set; }

    /// <summary>The key a hop authenticates with: its own entry, else the session key, else none.</summary>
    public string? KeyForHop(string hop)
    {
        if (JumpKeys is not null)
        {
            var wanted = hop.Trim();
            foreach (var pair in JumpKeys)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value)
                    && string.Equals(pair.Key.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
        }
        return string.IsNullOrWhiteSpace(KeyPath) ? null : KeyPath;
    }

    /// <summary>True when at least one stage of the chain has a key of its own.</summary>
    public bool HasPerHopKeys => JumpKeys is not null && JumpKeys.Values.Any(v => !string.IsNullOrWhiteSpace(v));
    public string? ProxyCommand { get; set; }

    public bool ForwardAgent { get; set; }
    public bool Compression { get; set; }
    public bool StrictHostKeyChecking { get; set; } = true;

    /// <summary>Lets the agent verbs use this session while the agent gate is on.</summary>
    public bool AllowAgent { get; set; }
    public bool X11Forwarding { get; set; }

    /// <summary>Ssh | Local | Telnet | Serial | Tmux. Defaults to Ssh so old session files still load.</summary>
    public string Kind { get; set; } = "Ssh";

    /// <summary>
    /// The tmux session name for Kind == "Tmux"; blank attaches to the most recent session. The SSH
    /// settings on this session are the transport, so keys, jump hosts and agent forwarding apply.
    /// </summary>
    public string? TmuxSession { get; set; }

    /// <summary>Serial transport settings (Kind == "Serial").</summary>
    public string? SerialPortName { get; set; }
    public int SerialBaud { get; set; } = 115200;
    public int SerialDataBits { get; set; } = 8;
    public string SerialParity { get; set; } = "None";
    public string SerialStopBits { get; set; } = "One";
    public string SerialFlow { get; set; } = "None";

    /// <summary>Command sent once the shell is ready (optionally with {{vars}}).</summary>
    public string? StartupCommand { get; set; }

    public string? Notes { get; set; }
    public string? ColorTag { get; set; }
    public List<string> Tags { get; set; } = new();

    public bool MonitorEnabled { get; set; } = true;

    public DateTime LastUsed
    {
        get => _lastUsed;
        set => Set(ref _lastUsed, value);
    }

    public int UseCount { get; set; }

    [JsonIgnore]
    public bool IsOnline
    {
        get => _isOnline;
        set => Set(ref _isOnline, value);
    }

    [JsonIgnore]
    public string? LastError
    {
        get => _lastError;
        set => Set(ref _lastError, value);
    }

    [JsonIgnore]
    public string DisplayTarget => Kind switch
    {
        "Serial" => $"{SerialPortName ?? "COM?"} {SerialBaud} {SerialDataBits}"
                    + SerialTransport.ShortParity(SerialParity) + SerialTransport.ShortStop(SerialStopBits),
        "Telnet" => $"{Host}:{Port}",
        "Tmux" => (IsLocal ? "local" : string.IsNullOrWhiteSpace(User) ? Host : $"{User}@{Host}")
                  + (string.IsNullOrWhiteSpace(TmuxSession) ? " · tmux" : $" · tmux:{TmuxSession}"),
        _ => IsLocal
            ? "Local shell"
            : string.IsNullOrWhiteSpace(User)
                ? (UseConfigAlias && !string.IsNullOrWhiteSpace(ConfigAlias) ? ConfigAlias! : Host)
                : $"{User}@{Host}"
    };

    /// <summary>A local shell instead of an SSH connection (PowerShell on this machine).</summary>
    public bool IsLocal { get; set; }

    public string? LocalShell { get; set; }

    [JsonIgnore]
    public string PortLabel => Port == 22 ? "" : $":{Port}";

    [JsonIgnore]
    public string Subtitle => UseConfigAlias && !string.IsNullOrWhiteSpace(ConfigAlias)
        ? $"ssh {ConfigAlias}"
        : $"{DisplayTarget}{PortLabel}";

    public Session Clone()
    {
        var copy = (Session)MemberwiseClone();
        // A memberwise clone shares the list, so editing one session's tags would edit its duplicate.
        copy.Tags = new List<string>(Tags ?? new List<string>());
        return copy;
    }

    /// <summary>
    /// Everything the session editor owns: every public writable property except identity and live
    /// state. This is a copy of the properties rather than a hand-kept field list on purpose — a list
    /// in the window silently stopped applying edits as soon as the model grew a field it did not know
    /// about (colour tags, kind, the serial settings and X11 forwarding were all being dropped).
    /// </summary>
    public void CopyEditableFrom(Session other)
    {
        foreach (var property in typeof(Session).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            // Name's getter falls back to DisplayTarget; copying it would freeze an imported session's
            // empty name into "user@host" and stop it tracking later host/user edits.
            if (property.Name is nameof(Id) or nameof(LastUsed) or nameof(UseCount)
                or nameof(IsOnline) or nameof(LastError) or nameof(Name)) continue;
            property.SetValue(this, property.GetValue(other));
        }

        // Name is skipped above because its getter falls back to DisplayTarget. Copy the STORED name
        // instead, so a rename still lands and an empty stored name stays empty (and keeps tracking).
        Name = other._name;
    }
}

/// <summary>A folder in the session tree.</summary>
public sealed class SessionFolder : Observable
{
    private string _name = "New folder";
    private bool _isExpanded = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string? ParentId { get; set; }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
}

/// <summary>A reusable command.</summary>
public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Description { get; set; }
    public string? ColorTag { get; set; }

    /// <summary>Optional grouping label shown in the snippet list; null or empty means ungrouped.</summary>
    public string? Folder { get; set; }
}

/// <summary>
/// One keyword-highlighting rule. Colours are hex strings so the rule survives JSON
/// round-trips; an empty colour means "leave it alone".
/// </summary>
public sealed class HighlightRule
{
    public string Pattern { get; set; } = "";

    /// <summary>Wash painted behind the match, e.g. "#5A1F1F". Empty = no wash.</summary>
    public string Background { get; set; } = "";

    /// <summary>Colour the matched glyphs are redrawn in, e.g. "#FF8A8A". Empty = keep.</summary>
    public string Foreground { get; set; } = "";

    public bool IgnoreCase { get; set; } = true;
    public bool IsRegex { get; set; }
    public bool Bold { get; set; }
    public bool Enabled { get; set; } = true;
}

public enum TunnelKind { Local, Remote, Dynamic }

/// <summary>A saved port-forwarding rule.</summary>
public sealed class TunnelDef : Observable
{
    private bool _isActive;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string SessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public TunnelKind Kind { get; set; } = TunnelKind.Local;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; } = 8080;
    public string DestHost { get; set; } = "localhost";
    public int DestPort { get; set; } = 80;
    public bool AutoStart { get; set; }

    [JsonIgnore]
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    [JsonIgnore]
    public string Summary => Kind switch
    {
        TunnelKind.Local => $"L  {BindAddress}:{BindPort}  →  {DestHost}:{DestPort}",
        TunnelKind.Remote => $"R  {BindAddress}:{BindPort}  ←  {DestHost}:{DestPort}",
        _ => $"D  {BindAddress}:{BindPort}  (SOCKS5)"
    };
}
