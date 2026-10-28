using System.Globalization;
using System.Text;

namespace ScumStudio.Modding.ServerMods;

/// <summary>What the bridge is asked to do.</summary>
public enum BridgeKind
{
    /// <summary>A message on one player's screen (<c>controller:SendHUDMessageToClient</c>).</summary>
    Message,

    /// <summary>A message on every player's screen (<c>gamemode:SendHUDMessageToAll</c>).</summary>
    Say,

    /// <summary>An admin command (what an admin types in chat after <c>#</c>), run through the channel of an online admin.</summary>
    Admin,
}

/// <summary>
/// The studio bridge: a small Lua mod (<see cref="Code"/>) that runs inside the dedicated server and carries out what the
/// Studio's console sends. The Studio and the mod meet in files of the mod's folder, so nothing is opened on the network:
/// the Studio appends lines to <c>commands.txt</c> (<c>id TAB kind TAB target TAB text</c>), the mod takes the file every
/// second, runs the lines and writes <c>results.log</c>; every few seconds it writes the players online and where they are
/// to <c>players.txt</c> (<c>name TAB X TAB Y TAB Z</c>) and a line to <c>events.log</c> when somebody joins or leaves. An admin command has to go through the chat channel of a player who is an admin (the game checks the
/// sender), so it works while such a player is online.
/// </summary>
public static class ServerBridge
{
    /// <summary>The mod's name (its folder under <c>ue4ss\Mods</c>).</summary>
    public const string ModName = "StudioBridge";

    /// <summary>The bridge's folder.</summary>
    public static string Folder(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Path.Combine(server.ModsFolder, ModName);
    }

    /// <summary>The file the Studio appends commands to.</summary>
    public static string QueueFile(ServerInstall server) => Path.Combine(Folder(server), "commands.txt");

    /// <summary>The file the mod writes what it did to.</summary>
    public static string ResultsFile(ServerInstall server) => Path.Combine(Folder(server), "results.log");

    /// <summary>The file the mod appends a line to when somebody joins or leaves (<c>time TAB join|leave TAB name TAB X TAB Y TAB Z</c>).</summary>
    public static string EventsFile(ServerInstall server) => Path.Combine(Folder(server), "events.log");

    /// <summary>The file with the names of the players online.</summary>
    public static string PlayersFile(ServerInstall server) => Path.Combine(Folder(server), "players.txt");

    /// <summary>True when the bridge mod is in the server's mods and switched on.</summary>
    public static bool IsInstalled(ServerInstall server) =>
        new ServerModStore(server).List().Any(m => string.Equals(m.Name, ModName, StringComparison.OrdinalIgnoreCase) && m.Enabled);

    /// <summary>Writes the bridge mod into the server and switches it on (a new mod loads at the next server start).</summary>
    public static void Install(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        new ServerModStore(server).Save(ModName, Code);
    }

    /// <summary>Appends a command for the mod; returns its id (the first column of the line in <c>results.log</c>).</summary>
    /// <param name="server">The server.</param>
    /// <param name="kind">What to do.</param>
    /// <param name="target">The player's name for <see cref="BridgeKind.Message"/>; for <see cref="BridgeKind.Admin"/> the admin player's name (empty: the first one online).</param>
    /// <param name="text">The message, or the command (a leading <c>#</c> is dropped).</param>
    /// <exception cref="ArgumentException">The text is empty, or a message has no player.</exception>
    public static string Enqueue(ServerInstall server, BridgeKind kind, string? target, string text)
    {
        ArgumentNullException.ThrowIfNull(server);
        var clean = Clean(text);
        if (kind == BridgeKind.Admin)
        {
            clean = clean.TrimStart('#').Trim();
        }

        if (clean.Length == 0)
        {
            throw new ArgumentException("There is nothing to send.", nameof(text));
        }

        var who = Clean(target ?? string.Empty);
        if (kind == BridgeKind.Message && who.Length == 0)
        {
            throw new ArgumentException("Pick the player the message is for.", nameof(target));
        }

        var id = DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture);
        var line = string.Join('\t', id, kind.ToString().ToLowerInvariant(), who, clean) + "\n";
        Directory.CreateDirectory(Folder(server));
        using var stream = new FileStream(QueueFile(server), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new UTF8Encoding(false).GetBytes(line);
        stream.Write(bytes, 0, bytes.Length);
        return id;
    }

    /// <summary>The names of the players online, as the mod last wrote them (empty when the server is off or the mod is not running).</summary>
    public static IReadOnlyList<string> ReadPlayers(ServerInstall server)
    {
        try
        {
            var file = PlayersFile(server);
            return File.Exists(file)
                ? [.. File.ReadAllLines(file, Encoding.UTF8).Select(l => l.Split('	')[0].Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The players online with the place the mod last saw them at (the file's lines are <c>name TAB X TAB Y TAB Z</c>).</summary>
    public static IReadOnlyList<DashboardPlayer> ReadPlayerPositions(ServerInstall server)
    {
        try
        {
            var file = PlayersFile(server);
            if (!File.Exists(file))
            {
                return [];
            }

            double Number(string[] parts, int i) => parts.Length > i && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            return [.. File.ReadAllLines(file, Encoding.UTF8).Select(l => l.Split('	')).Where(p => p[0].Trim().Length > 0)
                .Select(p => new DashboardPlayer(p[0].Trim(), string.Empty, string.Empty, Number(p, 1), Number(p, 2), Number(p, 3), true))
                .DistinctBy(p => p.Name, StringComparer.Ordinal).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>When the mod last wrote the player list (UTC), or null.</summary>
    public static DateTime? PlayersWrittenAt(ServerInstall server)
    {
        var file = PlayersFile(server);
        return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
    }

    /// <summary>One line of the file: no tabs and no line breaks (they separate the columns and the lines).</summary>
    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>The mod's <c>main.lua</c>. Only functions that were called on the running server are used (see <see cref="LuaApiCatalog"/>).</summary>
    public const string Code = """
        -- STUDIO BRIDGE: carries out what the Studio's console sends (messages to a player or to everyone, admin commands)
        -- and writes who is online. It talks to the Studio through the files in this folder; nothing is opened on the network.
        local DIR = "ue4ss/Mods/StudioBridge/"
        local QUEUE, RESULTS, PLAYERS = DIR .. "commands.txt", DIR .. "results.log", DIR .. "players.txt"
        local CHECK_MS = 1000        -- how often the command file is looked at
        local PLAYERS_EVERY = 5      -- the player list is written every ... checks
        local counter = 0

        local function note(id, ok, text)
          pcall(function()
            local f = io.open(RESULTS, "a")
            if f then f:write(os.date("%H:%M:%S"), "\t", id, "\t", ok and "ok" or "error", "\t", tostring(text), "\n") f:close() end
          end)
        end

        local function nameOf(pc)
          local ok, name = pcall(function() return pc.PlayerState.PlayerNamePrivate:ToString() end)
          if ok and name and name ~= "" then return name end
          return nil
        end

        local function controllers()
          local list = {}
          local all = FindAllOf("BP_ConZPlayerController_C")
          if all then
            for _, pc in ipairs(all) do
              if pc:IsValid() then list[#list + 1] = pc end
            end
          end
          return list
        end

        -- Where each player stands, by name: the characters (BP_Prisoner_C) know their place, their controller the name.
        local function placesByName()
          local places = {}
          local all = FindAllOf("BP_Prisoner_C")
          if all then
            for _, body in ipairs(all) do
              pcall(function()
                if body:IsValid() then
                  local pc = body:GetController()
                  local name = (pc and pc:IsValid()) and nameOf(pc) or nil
                  if name then
                    local at = body:K2_GetActorLocation()
                    places[name] = {at.X, at.Y, at.Z}
                  end
                end
              end)
            end
          end
          return places
        end

        local EVENTS = DIR .. "events.log"
        local known = {}
        local function event(kind, name, x, y, z)
          pcall(function()
            local f = io.open(EVENTS, "a")
            if f then
              f:write(os.date("%Y.%m.%d-%H.%M.%S"), "\t", kind, "\t", name, "\t", string.format("%.0f", x), "\t", string.format("%.0f", y), "\t", string.format("%.0f", z), "\n")
              f:close()
            end
          end)
        end

        -- Who is online and where, every few seconds (players.txt), and a line in events.log when somebody comes or goes.
        local function writePlayers()
          local lines, now = {}, {}
          local places = placesByName()
          for _, pc in ipairs(controllers()) do
            local name = nameOf(pc)
            if name then
              local at = places[name] or {0, 0, 0}
              local x, y, z = at[1], at[2], at[3]
              lines[#lines + 1] = string.format("%s\t%.0f\t%.0f\t%.0f", name, x, y, z)
              now[name] = {x, y, z}
              if not known[name] then event("join", name, x, y, z) end
            end
          end
          for name, at in pairs(known) do
            if not now[name] then event("leave", name, at[1], at[2], at[3]) end
          end
          known = now
          local f = io.open(PLAYERS, "w")
          if f then f:write(table.concat(lines, "\n"), "\n") f:close() end
        end

        local function findPlayer(name)
          local wanted = string.lower(name)
          for _, pc in ipairs(controllers()) do
            local n = nameOf(pc)
            if n and string.lower(n) == wanted then return pc end
          end
          return nil
        end

        local function run(id, kind, target, text)
          if kind == "say" then
            local gm = FindFirstOf("BP_ConZGameMode_C")
            if not (gm and gm:IsValid()) then return note(id, false, "no game mode") end
            gm:SendHUDMessageToAll(text, false)
            return note(id, true, "to everyone: " .. text)
          elseif kind == "message" then
            local pc = findPlayer(target)
            if not pc then return note(id, false, "player not online: " .. target) end
            pc:SendHUDMessageToClient(text, nil)
            return note(id, true, "to " .. target .. ": " .. text)
          elseif kind == "admin" then
            -- The game checks that the sender is an admin, so the command goes through an admin player's chat channel.
            local channels = FindAllOf("PlayerRpcChannel")
            if channels then
              for _, ch in ipairs(channels) do
                if ch:IsValid() then
                  local outer = ch:GetOuter()
                  local n = (outer and outer:IsValid()) and nameOf(outer) or nil
                  if n and (target == "" or string.lower(n) == string.lower(target)) then
                    ch:Chat_Server_ProcessAdminCommand(text)
                    return note(id, true, "#" .. text .. " (through " .. n .. ", who must be an admin)")
                  end
                end
              end
            end
            return note(id, false, "no player online to send it through" .. (target ~= "" and (": " .. target) or ""))
          end
          return note(id, false, "unknown kind " .. tostring(kind))
        end

        local function takeQueue()
          local work = QUEUE .. ".work"
          os.remove(work)
          if not os.rename(QUEUE, work) then return nil end
          local f = io.open(work, "r")
          if not f then return nil end
          local text = f:read("*a")
          f:close()
          os.remove(work)
          return text
        end

        local function tick()
          local text = takeQueue()
          if text then
            for line in string.gmatch(text, "[^\n]+") do
              local id, kind, target, body = string.match(line, "^([^\t]*)\t([^\t]*)\t([^\t]*)\t(.*)$")
              if id then
                local ok, err = pcall(run, id, kind, target, body)
                if not ok then note(id, false, err) end
              end
            end
          end
          counter = counter + 1
          if counter % PLAYERS_EVERY == 0 then pcall(writePlayers) end
        end

        LoopAsync(CHECK_MS, function()
          ExecuteInGameThread(function() pcall(tick) end)
          return false
        end)
        print("[StudioBridge] on\n")
        """;
}
