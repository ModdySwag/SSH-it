using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshIt.Terminal;

namespace SshIt.Services;

/// <summary>One command block inside a recording: when it started and ended, on the recording's clock.</summary>
public sealed class CastBlock
{
    public int Block { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public string Command { get; set; } = "";
    public int Exit { get; set; } = -1;
    public string Cwd { get; set; } = "";
    public string Host { get; set; } = "";
    public string Session { get; set; } = "";
    public string Started { get; set; } = "";

    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, End - Start));

    public string Label
        => Clock(Start) + " · " + (Command.Length == 0 ? "(no command)" : Command)
           + (Exit >= 0 ? " · exit " + Exit : "");

    public static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)span.TotalMinutes).ToString("00") + ":" + span.Seconds.ToString("00");
    }
}

/// <summary>
/// R3-15: block index for a recording. The .cast holds output only, so a sidecar JSON file beside it
/// records where each command block starts and ends on the recording's clock — that is what lets the
/// player jump to a block (and a receipt cite one) without parsing the terminal stream again.
///
/// Honest limits: the sidecar is not part of the .cast, so copying the recording alone loses the
/// index (it can be rebuilt from the escape stream only by replaying it), and a block only appears if
/// the shell emitted OSC 133 marks, i.e. shell integration was on.
/// </summary>
public static class CastIndex
{
    public static string PathFor(string castPath) => castPath + ".blocks.json";

    public static List<CastBlock> Load(string castPath)
    {
        var path = PathFor(castPath);
        var blocks = new List<CastBlock>();
        if (!File.Exists(path)) return blocks;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document) return blocks;
            if (document["blocks"] is not JsonArray rows) return blocks;
            foreach (var row in rows.OfType<JsonObject>())
                blocks.Add(new CastBlock
                {
                    Block = row["block"]?.GetValue<int>() ?? 0,
                    Start = row["start"]?.GetValue<double>() ?? 0,
                    End = row["end"]?.GetValue<double>() ?? 0,
                    Command = row["command"]?.GetValue<string>() ?? "",
                    Exit = row["exit"]?.GetValue<int>() ?? -1,
                    Cwd = row["cwd"]?.GetValue<string>() ?? "",
                    Host = row["host"]?.GetValue<string>() ?? "",
                    Session = row["session"]?.GetValue<string>() ?? "",
                    Started = row["started"]?.GetValue<string>() ?? ""
                });
        }
        catch (Exception ex)
        {
            Log.Warn("cast index: " + ex.Message);
        }

        blocks = blocks.OrderBy(b => b.Start).ToList();

        /* A mark's own duration is often a few milliseconds: the shell reports when the command
           finished, and the wait at the prompt belongs to no block. For playback the useful window is
           "from this block's start until the next one", so a playhead is always inside a block. */
        for (var i = 0; i < blocks.Count; i++)
        {
            var next = i + 1 < blocks.Count ? blocks[i + 1].Start : blocks[i].Start + 0.5;
            if (blocks[i].End < next - 0.001) blocks[i].End = Math.Max(blocks[i].End, next - 0.001);
        }

        return blocks;
    }

    public static void Save(string castPath, List<CastBlock> blocks)
    {
        var array = new JsonArray();
        foreach (var block in blocks)
            array.Add(new JsonObject
            {
                ["block"] = block.Block,
                ["start"] = Math.Round(block.Start, 3),
                ["end"] = Math.Round(block.End, 3),
                ["command"] = block.Command,
                ["exit"] = block.Exit,
                ["cwd"] = block.Cwd,
                ["host"] = block.Host,
                ["session"] = block.Session,
                ["started"] = block.Started
            });

        var document = new JsonObject
        {
            ["version"] = 1,
            ["cast"] = Path.GetFileName(castPath),
            ["blocks"] = array
        };

        var path = PathFor(castPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        // A recording is live: the player may be reading the index while the pane appends to it.
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Adds one finished block to the sidecar. Never throws into the pane.</summary>
    public static void Append(string castPath, CastBlock block)
    {
        try
        {
            var blocks = Load(castPath);
            if (blocks.Any(b => b.Block == block.Block)) return;
            blocks.Add(block);
            Save(castPath, blocks.OrderBy(b => b.Start).ToList());
        }
        catch (Exception ex)
        {
            Log.Warn("cast index append: " + ex.Message);
        }
    }

    /// <summary>Turns one live block into an index row, on the recording's clock.</summary>
    public static CastBlock FromMark(PromptMark mark, int block, DateTime recordStart, Models.Session session)
    {
        var start = Math.Max(0, (mark.Started.ToUniversalTime() - recordStart).TotalSeconds);
        return new CastBlock
        {
            Block = block,
            Start = Math.Round(start, 3),
            End = Math.Round(start + Math.Max(0, mark.Duration.TotalSeconds), 3),
            Command = mark.Command ?? "",
            Exit = mark.ExitCode,
            Cwd = mark.Cwd ?? "",
            Host = session.Host,
            Session = session.Id,
            Started = mark.Started.ToUniversalTime().ToString("O")
        };
    }

    /// <summary>The block a playhead is inside, or null when it is between blocks.</summary>
    public static CastBlock? At(List<CastBlock> blocks, double seconds)
        => blocks.FirstOrDefault(b => seconds >= b.Start && seconds <= b.End);

    public static string ToText(string castPath, List<CastBlock> blocks)
    {
        if (blocks.Count == 0) return "no block index beside " + castPath
                                     + " (the shell must emit OSC 133 marks while recording)";
        var lines = new List<string>
        {
            blocks.Count + " block(s) in " + Path.GetFileName(castPath)
        };
        foreach (var block in blocks)
            lines.Add(block.Block.ToString().PadLeft(3) + "  " + CastBlock.Clock(block.Start) + "–"
                      + CastBlock.Clock(block.End) + "  exit " + block.Exit.ToString().PadLeft(3) + "  "
                      + (block.Command.Length == 0 ? "(no command)" : block.Command));
        return string.Join("\n", lines);
    }

    public static string ToJson(string castPath, List<CastBlock> blocks) => JsonSerializer.Serialize(new
    {
        cast = castPath,
        blocks = blocks.Select(b => new
        {
            block = b.Block, start = b.Start, end = b.End, command = b.Command,
            exit = b.Exit, cwd = b.Cwd, host = b.Host, session = b.Session, started = b.Started,
            duration_ms = (int)b.Duration.TotalMilliseconds
        })
    });
}
