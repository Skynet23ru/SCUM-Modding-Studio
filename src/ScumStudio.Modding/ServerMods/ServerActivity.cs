using System.Globalization;
using System.Text;

namespace ScumStudio.Modding.ServerMods;

/// <summary>One thing a player did in the game, as the activity mod logged it.</summary>
/// <param name="Time">When (server time, <c>yyyy.MM.dd-HH.mm.ss</c>).</param>
/// <param name="Kind">What: pickup, drop, move, open, craft, place, lockpick, sell, buy, die … (or <c>hook_failed</c> / <c>start</c> for the mod itself).</param>
/// <param name="Player">Who.</param>
/// <param name="X">Where, X (cm).</param>
/// <param name="Y">Y (cm).</param>
/// <param name="Z">Z (cm).</param>
/// <param name="Detail">What it was about: the objects the game call named (items, the container or vehicle they came from or went to).</param>
public sealed record ActivityEvent(string Time, string Kind, string Player, double X, double Y, double Z, string Detail);

/// <summary>The detail of an activity line taken apart: what item, which container it went to and where it came from.</summary>
/// <param name="Item">The item's type (<c>Emergency_bandage_Big_Deluxe_C</c>), or empty.</param>
/// <param name="ItemId">The item's id in the game, or empty.</param>
/// <param name="Stats">Its numbers (<c>health=100/100 weight=13 …</c>), or empty.</param>
/// <param name="To">The container the item went to or was used in (a vehicle's trunk: <c>Vehicle BPC_Rager1_C #2147336586</c>), or empty.</param>
/// <param name="From">Where the item was before (<c>World</c>, a trunk, the player's pockets), or empty.</param>
/// <param name="ToX">The place of the container's owner (a vehicle), X (cm), when the line has it.</param>
/// <param name="ToY">Y (cm).</param>
/// <param name="HasToPlace">True when <paramref name="ToX"/> and <paramref name="ToY"/> are known.</param>
public sealed record ActivityFields(string Item, string ItemId, string Stats, string To, string From, double ToX = 0, double ToY = 0, bool HasToPlace = false);

/// <summary>Who last used a vehicle, as the activity log tells it.</summary>
/// <param name="Player">The player.</param>
/// <param name="Time">When (server time of the log line).</param>
/// <param name="Kind">What they did with it (the log's action: drive, move, pickup …).</param>
/// <param name="DistanceCm">How far the vehicle was from the place asked for (cm).</param>
public sealed record VehicleUse(string Player, string Time, string Kind, double DistanceCm);

/// <summary>
/// The game stores no owner of a vehicle, so "the owner" is the player the activity log saw using it last: driving it, or putting
/// something into its trunk or taking something out. A vehicle of the save (type and place) is matched to the lines that name a
/// vehicle of the same type standing near that place.
/// </summary>
public static partial class VehicleUsers
{
    /// <summary>The vehicle mentions of the events: what <c>BPC_Rager1_C id=3 @481623 -237235</c> says in a detail.</summary>
    public static IReadOnlyList<(string Type, double X, double Y, ActivityEvent Event)> Mentions(IEnumerable<ActivityEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = new List<(string, double, double, ActivityEvent)>();
        foreach (var e in events)
        {
            if (e.Player.Length == 0 || e.Player == "?")
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match m in VehicleMention().Matches(e.Detail))
            {
                if (double.TryParse(m.Groups["x"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(m.Groups["y"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                {
                    list.Add((m.Groups["type"].Value, x, y, e));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// The player who used the vehicle of <paramref name="type"/> standing at (<paramref name="x"/>, <paramref name="y"/>) last,
    /// among <paramref name="mentions"/> (newest first) no farther than <paramref name="maxCm"/>; null when nobody was seen with it.
    /// </summary>
    public static VehicleUse? UserOf(string type, double x, double y, IReadOnlyList<(string Type, double X, double Y, ActivityEvent Event)> mentions, double maxCm = 15000)
    {
        ArgumentNullException.ThrowIfNull(mentions);
        foreach (var m in mentions)
        {
            if (!string.Equals(m.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var distance = Math.Sqrt(((m.X - x) * (m.X - x)) + ((m.Y - y) * (m.Y - y)));
            if (distance <= maxCm)
            {
                return new VehicleUse(m.Event.Player, m.Event.Time, m.Event.Kind, distance);
            }
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<type>BPC_[A-Za-z0-9_]+?)_C id=\d+ @(?<x>-?[\d.]+) (?<y>-?[\d.]+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VehicleMention();
}

/// <summary>
/// The activity mod: a Lua mod (<see cref="Code"/>) that logs what the players do inside the dedicated server: who picked what
/// up, dropped it, moved it from one container to another (a vehicle's trunk, a chest, a corpse), opened, crafted, built,
/// lockpicked, sold, bought, died … with the place. It hooks the server-side game functions that run for these actions (found
/// in the server's class dump); the hook callback only keeps the objects it was given and the work is done a moment later in the
/// game thread (reading a player inside a hook crashed the server before). It writes <c>activity.log</c> in its folder, one
/// line <c>time TAB kind TAB player TAB X TAB Y TAB Z TAB detail</c>; the Dashboard shows it.
/// </summary>
public static class ServerActivity
{
    /// <summary>The mod's name (its folder under <c>ue4ss\Mods</c>).</summary>
    public const string ModName = "StudioActivity";

    /// <summary>The mod's folder.</summary>
    public static string Folder(ServerInstall server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Path.Combine(server.ModsFolder, ModName);
    }

    /// <summary>The log the mod writes.</summary>
    public static string LogFile(ServerInstall server) => Path.Combine(Folder(server), "activity.log");

    /// <summary>The newest events, newest first (empty when the mod has not logged anything).</summary>
    /// <param name="server">The server.</param>
    /// <param name="since">Only events at or after this log time (<c>yyyy.MM.dd-HH.mm.ss</c>); empty for all.</param>
    /// <param name="limit">How many at most.</param>
    public static IReadOnlyList<ActivityEvent> Read(ServerInstall server, string since = "", int limit = 500)
    {
        try
        {
            var file = LogFile(server);
            if (!File.Exists(file))
            {
                return [];
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The end of a big file is enough: the newest lines are what is asked for.
            const long tail = 8 * 1024 * 1024;
            if (stream.Length > tail)
            {
                stream.Seek(-tail, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n');
            var list = new List<ActivityEvent>();
            foreach (var line in lines.Reverse())
            {
                if (Parse(line.TrimEnd('\r')) is { } e && string.CompareOrdinal(e.Time, since) >= 0)
                {
                    list.Add(e);
                    if (list.Count >= limit)
                    {
                        break;
                    }
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>One line of the log, or null.</summary>
    public static ActivityEvent? Parse(string line)
    {
        var p = line.Split('\t');
        if (p.Length < 7 || p[0].Length != 19)
        {
            return null;
        }

        double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new ActivityEvent(p[0], p[1], p[2], Number(p[3]), Number(p[4]), Number(p[5]), string.Join('\t', p.Skip(6)));
    }

    /// <summary>
    /// Takes a line's detail apart: the item (type, id, numbers), the container and where the item was before. The detail is
    /// what the Lua mod writes: <c>item Type id=N {numbers} (was in Where) | to Where</c> (older lines: <c>container Kind:name in Where</c>); older lines
    /// (class names only) give just what they have.
    /// </summary>
    public static ActivityFields Describe(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        string item = string.Empty, id = string.Empty, stats = string.Empty, to = string.Empty, from = string.Empty;
        double toX = 0, toY = 0;
        var hasPlace = false;
        foreach (var piece in detail.Split(" | "))
        {
            var text = piece.Trim();
            var by = text.IndexOf(" || ", StringComparison.Ordinal);
            if (by >= 0)
            {
                text = text[..by];
            }

            if (text.StartsWith("item ", StringComparison.Ordinal))
            {
                var open = text.IndexOf('{', StringComparison.Ordinal);
                var close = open >= 0 ? text.IndexOf('}', open) : -1;
                var head = (open >= 0 ? text[5..open] : text[5..]).Trim();
                var split = head.IndexOf(" id=", StringComparison.Ordinal);
                item = split >= 0 ? head[..split] : head;
                id = split >= 0 ? head[(split + 4)..].Trim() : string.Empty;
                stats = open >= 0 && close > open ? text[(open + 1)..close].Trim() : string.Empty;
                if (text.IndexOf("(was in ", StringComparison.Ordinal) is var was and >= 0)
                {
                    from = Place(text[(was + 8)..].TrimEnd(')'));
                }
            }
            else if (text.StartsWith("to ", StringComparison.Ordinal))
            {
                to = Place(text[3..]);
                (toX, toY, hasPlace) = PlaceOf(text, hasPlace, toX, toY);
            }
            else if (text.StartsWith("container ", StringComparison.Ordinal))
            {
                var inside = text.IndexOf(" in ", StringComparison.Ordinal);
                var kind = (inside >= 0 ? text[10..inside] : text[10..]).Trim();
                to = Place(inside >= 0 ? text[(inside + 4)..] : string.Empty, kind);
                (toX, toY, hasPlace) = PlaceOf(text, hasPlace, toX, toY);
            }
        }

        return new ActivityFields(item, id, stats, to, from, toX, toY, hasPlace);
    }

    /// <summary>The <c>@x y z</c> of a container's owner in a piece of the detail (the vehicle's place), else what was known.</summary>
    private static (double X, double Y, bool Has) PlaceOf(string text, bool has, double x, double y)
    {
        var at = text.LastIndexOf(" @", StringComparison.Ordinal);
        if (at < 0)
        {
            return (x, y, has);
        }

        var parts = text[(at + 2)..].TrimEnd(')', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)
            ? (px, py, true)
            : (x, y, has);
    }

    /// <summary><c>Rager1_Item_Container_C id=7 of BPC_Rager1_C id=9 @1 2 3</c> → <c>Vehicle BPC_Rager1_C #9</c>; the world and the player's own are named.</summary>
    private static string Place(string where, string kind = "")
    {
        var text = where.Trim();
        if (text.Length == 0)
        {
            return kind;
        }

        if (text.StartsWith("Level:", StringComparison.Ordinal))
        {
            return "World";
        }

        // "VehicleInventoryComponent:Inventory in Rager1_Item_Container_C id=7 of BPC_Rager1_C id=9": the component's kind, then whose it is.
        if (text.IndexOf(" in ", StringComparison.Ordinal) is var inside and > 0 && text[..inside].Contains(':', StringComparison.Ordinal) && !text.StartsWith("Level:", StringComparison.Ordinal))
        {
            kind = text[..inside];
            text = text[(inside + 4)..];
        }

        var of = text.IndexOf(" of ", StringComparison.Ordinal);
        var owner = of >= 0 ? text[(of + 4)..] : text;
        var at = owner.IndexOf(" @", StringComparison.Ordinal);
        if (at >= 0)
        {
            owner = owner[..at];
        }

        var id = owner.IndexOf(" id=", StringComparison.Ordinal);
        var who = id >= 0 ? owner[..id] + " #" + owner[(id + 4)..] : owner;
        var label = owner.Contains("Prisoner", StringComparison.Ordinal) || owner.Contains("PlayerController", StringComparison.Ordinal) ? "Player"
            : owner.Contains("BPC_", StringComparison.Ordinal) || kind.StartsWith("Vehicle", StringComparison.Ordinal) ? "Vehicle"
            : string.Empty;
        return (label.Length > 0 ? label + " " : string.Empty) + who;
    }

    /// <summary>The mod's <c>main.lua</c>.</summary>
    public const string Code = """
        -- STUDIO ACTIVITY: logs what players do in the game (pick up, drop, move between containers, open, craft, build,
        -- lockpick, sell, buy, die ...) with the player's name and place, to ue4ss/Mods/StudioActivity/activity.log.
        -- The Studio's Dashboard reads that file. A hook only keeps the objects the game gave it; the work is done a moment
        -- later in the game thread, because reading a player inside a hook crashed the server.
        local DIR = "ue4ss/Mods/StudioActivity/"
        local OUT = DIR .. "activity.log"
        local DELAY_MS = 400          -- how long after the action the line is written
        local MAX_PER_SECOND = 25     -- more than this many actions in a second are counted, not written
        local MAX_BYTES = 50 * 1024 * 1024
        local Inv  = "/Script/SCUM.InventoryUserComponent:"
        local Act  = "/Script/SCUM.InteractionComponent:"
        local Pris = "/Script/SCUM.Prisoner:"
        local Rpc  = "/Script/SCUM.PlayerRpcChannel:"

        -- function to hook, what to call it in the log
        local HOOKS = {
          { Inv .. "Server_CharacterInventoryComponent_PickupItem", "pickup" },
          { Inv .. "Server_CharacterInventoryComponent_PickupItems", "pickup" },
          { Inv .. "Server_CharacterInventoryComponent_PickupOrDropItem", "pickup_or_drop" },
          { Inv .. "Server_CharacterInventoryComponent_PickupOrDropItems", "pickup_or_drop" },
          { Inv .. "Server_CharacterInventoryComponent_DropItem", "drop" },
          { Inv .. "Server_CharacterInventoryComponent_DropItems", "drop" },
          { Inv .. "Server_CharacterInventoryComponent_DropItemAt", "drop" },
          { Inv .. "Server_CharacterInventoryComponent_EquipClothes", "equip" },
          { Inv .. "Server_CharacterInventoryComponent_UnequipClothes", "unequip" },
          { Inv .. "Server_InventoryComponent_AddOrMoveEntry", "move" },
          { Inv .. "Server_InventoryComponent_RemoveEntry", "remove" },
          { Inv .. "Server_OpenInventory", "open_inventory" },
          { Act .. "PickupItemOnServer", "pickup_world" },
          { Act .. "OpenItemOnServer", "open" },
          { Act .. "UseItemOnServer", "use" },
          { Act .. "Server_CraftItem", "craft" },
          { Act .. "Server_UncraftItem", "uncraft" },
          { Act .. "Server_RemoveLockOnServer", "lock_removed" },
          { Pris .. "Server_DropItemFromHands", "drop_hands" },
          { Pris .. "Server_DestroyPlaceable", "destroy_placeable" },
          { Pris .. "Server_InitiateCraftedPlacement", "build" },
          { Pris .. "Server_lockpickingEnded", "lockpick" },
          { "/Script/SCUM.PlaceableActorBase:Server_Place", "place" },
          { Rpc .. "Server_PlayerPurchaseTradeable", "buy" },
          { Rpc .. "Server_PlayerSellTradeables", "sell" },
          { Rpc .. "Server_UnpackItem", "unpack" },
          { Rpc .. "Cooking_Server_CreateResultItemAndAssignToPrisoner", "cook" },
          { Rpc .. "InteractItemWithItemOnServer", "item_on_item" },
          { Rpc .. "TrapItem_Server_DisarmTrap", "disarm_trap" },
          { Rpc .. "Server_HackAbandonedBunkerDialPad", "hack_bunker_pad" },
          { Rpc .. "Server_HackAbandonedBunkerDoor", "hack_bunker_door" },
          { Rpc .. "Vehicle_Server_DriverShouldBecomeOwner", "drive" },
          { Rpc .. "Prisoner_Server_Die", "die" },
        }

        local places = {}     -- player name -> {x, y, z}, looked up every few seconds
        local recent, dropped = {}, 0

        local function write(kind, who, x, y, z, detail)
          pcall(function()
            local f = io.open(OUT, "a")
            if f then
              f:write(os.date("%Y.%m.%d-%H.%M.%S"), "\t", kind, "\t", who or "", "\t", string.format("%.0f", x or 0), "\t",
                string.format("%.0f", y or 0), "\t", string.format("%.0f", z or 0), "\t", (detail or ""):gsub("[\t\r\n]", " "), "\n")
              f:close()
            end
          end)
        end

        local function try(f)
          local ok, v = pcall(function()
            local r = f()
            if r ~= nil and r:IsValid() then return r end
            return nil
          end)
          if ok then return v end
          return nil
        end

        local function nameOf(controller)
          local ok, name = pcall(function() return controller.PlayerState.PlayerNamePrivate:ToString() end)
          if ok and name and name ~= "" then return name end
          return nil
        end

        -- The player behind the object a hook ran on: a character, a controller, a component (its owner) or a channel (its outer).
        local function playerOf(obj)
          local candidates = {
            function() return obj end,
            function() return obj:GetOwner() end,
            function() return obj:GetOuter() end,
          }
          for _, get in ipairs(candidates) do
            local o = try(get)
            if o then
              local controller = nameOf(o) and o or try(function() return o:GetController() end)
              local name = controller and nameOf(controller) or nil
              if name then return name end
            end
          end
          return nil
        end

        -- "Emergency_bandage_Big_Deluxe_C_2147337687" -> "Emergency_bandage_Big_Deluxe_C id=2147337687"
        local function short(o)
          return pcall(function()
            local full = o:GetFullName()
            local class, path = string.match(full, "^(%S+) (.+)$")
            local leaf = string.match(path or full, "([^%.:]+)$") or full
            local base, id = string.match(leaf, "^(.-)_(%d+)$")
            if base then return base .. " id=" .. id end
            return (class or "?") .. ":" .. leaf
          end)
        end

        local function shortText(o)
          local ok, s = short(o)
          if ok then return s end
          return "?"
        end

        local function fmt(v)
          if type(v) == "number" then
            if v == math.floor(v) then return string.format("%d", v) end
            return string.format("%.2f", v)
          end
          return tostring(v)
        end

        local function placeText(o)
          local ok, s = pcall(function()
            local at = o:K2_GetActorLocation()
            return " @" .. string.format("%.0f %.0f %.0f", at.X, at.Y, at.Z)
          end)
          if ok then return s end
          return ""
        end

        -- What kind of thing holds something: a component says whose it is (a vehicle's trunk, a chest, a player's pockets).
        local function where(o)
          local s = shortText(o)
          local owner = try(function() return o:GetOwner() end)
          if owner then s = s .. " of " .. shortText(owner) .. placeText(owner) end
          return s
        end

        -- The numbers an item carries: health, weight and whatever properties are named like a quantity, use, fuel, water ...
        local KEYWORDS = { "health", "quantity", "usage", "uses", "amount", "stack", "fuel", "water", "ammo", "capacity", "condition",
                           "durab", "charge", "battery", "expir", "spoil", "temperature", "damage", "bullet", "rounds" }
        local function stats(item)
          local out = {}
          pcall(function() out[#out + 1] = "health=" .. fmt(item:GetHealth()) .. "/" .. fmt(item:GetMaxHealth()) end)
          pcall(function() out[#out + 1] = "weight=" .. fmt(item:GetTotalWeight()) end)
          pcall(function()
            item:GetClass():ForEachProperty(function(prop)
              pcall(function()
                if #out >= 14 then return end
                local pname = prop:GetFName():ToString()
                local low = string.lower(pname)
                for _, k in ipairs(KEYWORDS) do
                  if string.find(low, k, 1, true) then
                    local v = item[pname]
                    local t = type(v)
                    if t == "number" or t == "boolean" or t == "string" then out[#out + 1] = pname .. "=" .. fmt(v) end
                    break
                  end
                end
              end)
            end)
          end)
          return table.concat(out, " ")
        end

        -- One parameter of the game call, in words. Returns a table: text (what to write), item (the item's id when it is an item),
        -- stats, and container (where text) when it is a container.
        local function brief(v, was)
          local t = type(v)
          if t == "number" or t == "boolean" or t == "string" then return { text = tostring(v) } end
          if t ~= "userdata" then return { text = "(" .. t .. ")" } end
          local ok, r = pcall(function()
            if not v:IsValid() then return { text = "(gone)" } end
            local full = v:GetFullName()
            if string.find(full, "^ScriptStruct") or string.find(full, "^Package") then return nil end   -- a slot position: nothing to tell
            local st = stats(v)                      -- only an item answers GetHealth and GetTotalWeight
            if st ~= "" then
              local name = shortText(v)
              return { item = string.match(name, "id=(%d+)") or name, name = name, stats = st }
            end
            local lower = string.lower(full)
            if string.find(lower, "inventorycomponent", 1, true) or string.find(lower, "container", 1, true) then
              -- a trunk is in the vehicle's container item, which belongs to the vehicle
              local place = shortText(v) .. " in " .. (was and where(was) or where(v))
              return { container = place, kind = shortText(v) }
            end
            return { text = shortText(v) .. placeText(v) }   -- anything else (a vehicle): what it is and where it stands
          end)
          if ok then return r end
          return { text = "(unreadable)" }
        end

        -- The game does not tell where an item was before (an item is always an object of the level, whoever carries it), so the
        -- mod remembers where it saw each item last: picked up from the world, put into a trunk, taken out ...
        local known = {}
        local WORLD = "Level:PersistentLevel"
        local PICKUPS = { pickup_world = true, pickup = true }
        local DROPS = { drop = true, drop_hands = true }
        local last = { t = 0 }   -- the same line twice within a second (one click, several calls) is written once

        local function report(kind, self, params, outers)
          local name = playerOf(self)
          local at = name and places[name] or { 0, 0, 0 }
          local items, container, parts = {}, nil, {}
          for i = 1, #params do
            local b = brief(params[i], outers[i])
            if b then
              if b.item then items[#items + 1] = b
              elseif b.container then container = container or b
              elseif b.text then parts[#parts + 1] = b.text end
            end
          end
          local list = {}
          for _, b in ipairs(items) do
            local from = known[b.item]
            if not from then
              if PICKUPS[kind] then from = WORLD elseif DROPS[kind] and container then from = container.container else from = "?" end
            end
            local to = DROPS[kind] and WORLD or (container and container.container) or nil
            local text = "item " .. b.name .. " {" .. b.stats .. "} (was in " .. from .. ")"
            if to then text = text .. " | to " .. to end
            list[#list + 1] = text
            if to then known[b.item] = to end
          end
          if #items == 0 and container then list[#list + 1] = "container " .. container.container end
          for _, p in ipairs(parts) do list[#list + 1] = p end
          local detail = table.concat(list, " | ")
          local key = kind .. (name or "?") .. detail
          local now = os.time()
          if last.t ~= now then last = { t = now } end
          if last[key] then return end
          last[key] = true
          write(kind, name or "?", at[1], at[2], at[3], detail)
        end

        -- A one-time list of the properties of the classes that hold items, so the next version of this mod can read more
        -- (the game's reflection data has only the functions): ue4ss/Mods/StudioActivity/properties.txt.
        local function dumpProperties()
          local f = io.open(DIR .. "properties.txt", "w")
          if not f then return end
          for _, class in ipairs({ "Item", "InventoryUserComponent", "InventoryComponent", "GridInventoryComponent", "VehicleInventoryComponent", "PrisonerInventoryComponent", "ItemContainer", "StackableItemComponent", "DiscreteUsageItemComponent" }) do
            pcall(function()
              local cls = StaticFindObject("/Script/SCUM." .. class)
              local depth = 0
              while cls and cls:IsValid() and depth < 6 do
                f:write("== ", class, " / ", cls:GetFName():ToString(), "\n")
                cls:ForEachProperty(function(prop)
                  pcall(function() f:write("  ", prop:GetFName():ToString(), " : ", prop:GetClass():GetFName():ToString(), "\n") end)
                end)
                cls = cls:GetSuperStruct()
                depth = depth + 1
              end
            end)
          end
          f:close()
        end

        local function queue(kind, self, params, outers)
          local now = os.time()
          if recent.t ~= now then
            if dropped > 0 then write("dropped", "", 0, 0, 0, dropped .. " actions were not written (more than " .. MAX_PER_SECOND .. " a second)") end
            recent.t, recent.n, dropped = now, 0, 0
          end
          recent.n = recent.n + 1
          if recent.n > MAX_PER_SECOND then dropped = dropped + 1 return end
          ExecuteWithDelay(DELAY_MS, function()
            ExecuteInGameThread(function() pcall(report, kind, self, params, outers) end)
          end)
        end

        local function hook(path, kind)
          local ok, err = pcall(function()
            RegisterHook(path, function(self, ...)
              local args = { ... }
              local keptSelf, params, outers = nil, {}, {}
              pcall(function()
                keptSelf = self:get()
                for i = 1, math.min(#args, 6) do
                  local okp, v = pcall(function() return args[i]:get() end)
                  params[i] = okp and v or args[i]
                  -- Where an object was before the call (its outer is a plain pointer read): an item's old container.
                  pcall(function()
                    if type(params[i]) == "userdata" and params[i]:IsValid() then
                      local outer = params[i]:GetOuter()
                      if outer and outer:IsValid() then outers[i] = outer end
                    end
                  end)
                end
              end)
              if keptSelf ~= nil then queue(kind, keptSelf, params, outers) end
            end)
          end)
          if not ok then write("hook_failed", "", 0, 0, 0, path .. " " .. tostring(err)) end
        end

        local function refreshPlaces()
          local all = FindAllOf("BP_Prisoner_C")
          local now = {}
          if all then
            for _, body in ipairs(all) do
              pcall(function()
                if body:IsValid() then
                  local pc = body:GetController()
                  local name = (pc and pc:IsValid()) and nameOf(pc) or nil
                  if name then
                    local at = body:K2_GetActorLocation()
                    now[name] = { at.X, at.Y, at.Z }
                  end
                end
              end)
            end
          end
          places = now
        end

        -- A log that grew too big is put aside, so the disk does not fill up.
        pcall(function()
          local f = io.open(OUT, "r")
          if f then
            local size = f:seek("end")
            f:close()
            if size and size > MAX_BYTES then os.remove(OUT .. ".old") os.rename(OUT, OUT .. ".old") end
          end
        end)

        for _, h in ipairs(HOOKS) do hook(h[1], h[2]) end
        LoopAsync(5000, function()
          ExecuteInGameThread(function() pcall(refreshPlaces) end)
          return false
        end)
        ExecuteWithDelay(8000, function() ExecuteInGameThread(function() pcall(dumpProperties) end) end)
        write("start", "", 0, 0, 0, "StudioActivity on, " .. #HOOKS .. " hooks")
        print("[StudioActivity] on\n")
        """;
}
