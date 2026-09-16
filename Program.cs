using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:6000");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
var app = builder.Build();

string contentDir = Path.Combine(app.Environment.ContentRootPath, "content");
string motdPath = Path.Combine(contentDir, "motd.txt");
string dataDir = Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
string connectionString = "Data Source=" + Path.Combine(dataDir, "bassment.db");

using (SqliteConnection connection = new SqliteConnection(connectionString))
{
    connection.Open();
    using (SqliteCommand command = connection.CreateCommand())
    {
        command.CommandText = @"
        CREATE TABLE IF NOT EXISTS players (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Platform INTEGER NOT NULL,
            PlatformId INTEGER NOT NULL,
            Name TEXT NOT NULL,
            XP INTEGER NOT NULL DEFAULT 0,
            Level INTEGER NOT NULL DEFAULT 1,
            Reputation INTEGER NOT NULL DEFAULT 0,
            Verified INTEGER NOT NULL DEFAULT 0,
            Email TEXT NOT NULL DEFAULT '',
            UNIQUE (Platform, PlatformId)
        );
        CREATE TABLE IF NOT EXISTS images (
            PlayerId INTEGER PRIMARY KEY,
            Data BLOB NOT NULL
        );
        CREATE TABLE IF NOT EXISTS avatars (
            PlayerId INTEGER PRIMARY KEY,
            OutfitSelections TEXT NOT NULL DEFAULT '',
            SkinColor TEXT NOT NULL DEFAULT '',
            HairColor TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS settings (
            PlayerId INTEGER NOT NULL,
            Key TEXT NOT NULL,
            Value TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (PlayerId, Key)
        );
        CREATE TABLE IF NOT EXISTS avatar_items (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PlayerId INTEGER NOT NULL,
            AvatarItemDesc TEXT NOT NULL
        );";
        command.ExecuteNonQuery();
    }
    using (SqliteCommand command = connection.CreateCommand())
    {
        try
        {
            command.CommandText = "ALTER TABLE players ADD COLUMN Verified INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        catch { }
        try
        {
            command.CommandText = "ALTER TABLE players ADD COLUMN Email TEXT NOT NULL DEFAULT ''";
            command.ExecuteNonQuery();
        }
        catch { }
    }
}

string LoadMotd()
{
    try
    {
        if (File.Exists(motdPath)) {
            return File.ReadAllText(motdPath);
        }
    }
    catch { }
    return "Yikes, the server couldn't find a MOTD, and if it can't find a MOTD then you're probally BONED\n";
}

const string PlayerColumns = "Id, Platform, PlatformId, Name, XP, Level, Reputation, Verified";

Dictionary<string, object> ReadPlayer(SqliteDataReader reader)
{
    return new Dictionary<string, object>
    {
        ["Id"] = reader.GetInt64(0),
        ["Platform"] = reader.GetInt32(1),
        ["PlatformId"] = reader.GetInt64(2),
        ["Name"] = reader.GetString(3),
        ["XP"] = reader.GetInt32(4),
        ["Level"] = reader.GetInt32(5),
        ["Reputation"] = reader.GetInt32(6),
        ["Verified"] = reader.GetInt32(7)
    };
}

Dictionary<string, object> ToNovProfile(Dictionary<string, object> row)
{
    if (row.Count == 0)
    {
        return row;
    }
    string name = row["Name"].ToString() ?? "";
    bool verified = Convert.ToInt32(row["Verified"]) != 0;
    return new Dictionary<string, object>
    {
        ["Id"] = row["Id"],
        ["Username"] = name,
        ["DisplayName"] = name,
        ["XP"] = row["XP"],
        ["Level"] = row["Level"],
        ["Reputation"] = row["Reputation"],
        ["Verified"] = verified,
        ["Platform"] = row["Platform"],
        ["PlatformId"] = row["PlatformId"],
        ["Name"] = name
    };
}

void Execute(string sql, Action<SqliteParameterCollection> addParams)
{
    using (SqliteConnection connection = new SqliteConnection(connectionString))
    {
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            addParams(command.Parameters);
            command.ExecuteNonQuery();
        }
    }
}

T Query<T>(string sql, Func<SqliteDataReader, T> read, Action<SqliteParameterCollection> addParams)
{
    using (SqliteConnection connection = new SqliteConnection(connectionString))
    {
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            addParams(command.Parameters);
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return read(reader);
                }
            }
        }
    }
    return default(T);
}

Dictionary<string, object> FindPlayerById(long id)
{
    return Query("SELECT " + PlayerColumns + " FROM players WHERE Id = $id", ReadPlayer, p => p.AddWithValue("$id", id)) ?? new Dictionary<string, object>();
}

Dictionary<string, object> FindPlayerByPlatform(int platform, long platformId)
{
    return Query("SELECT " + PlayerColumns + " FROM players WHERE Platform = $p AND PlatformId = $pid", ReadPlayer, p =>
    {
        p.AddWithValue("$p", platform);
        p.AddWithValue("$pid", platformId);
    }) ?? new Dictionary<string, object>();
}

app.MapGet("/motd", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Text(LoadMotd(), "text/plain; charset=utf-8");
});

app.MapGet("/api/config/v1/motd", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Text(LoadMotd(), "text/plain; charset=utf-8");
});

app.MapGet("/api/players/v1/{id:long?}", (long? id, HttpRequest req) =>
{
    if (id.HasValue)
    {
        return Results.Json(ToNovProfile(FindPlayerById(id.Value)));
    }
    int platform = int.TryParse(req.Query["p"], out var pl) ? pl : 0;
    long platformId = long.TryParse(req.Query["id"], out var pid) ? pid : 0;
    return Results.Json(ToNovProfile(FindPlayerByPlatform(platform, platformId)));
});

app.MapPost("/api/players/v1/create", async (HttpRequest req) =>
{
    int platform = 0;
    long platformId = 0;
    string name = "";
    string contentType = req.ContentType ?? "";
    try
    {
        if (contentType.Contains("json"))
        {
            string jsonBody;
            using (var reader = new StreamReader(req.Body))
            {
                jsonBody = await reader.ReadToEndAsync();
            }
            app.Logger.LogInformation("POST /api/players/v1/create content-type=" + contentType + " body=" + jsonBody);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonBody);
            if (doc != null)
            {
                foreach (var kv in doc)
                {
                    string key = kv.Key.ToLowerInvariant();
                    if (key == "platform" && kv.Value.ValueKind == JsonValueKind.Number)
                    {
                        platform = kv.Value.GetInt32();
                    }
                    else if ((key == "platformid" || key == "id") && kv.Value.ValueKind == JsonValueKind.Number)
                    {
                        platformId = kv.Value.GetInt64();
                    }
                    else if (key == "name" && kv.Value.ValueKind == JsonValueKind.String)
                    {
                        name = kv.Value.GetString() ?? "";
                    }
                }
            }
        }
        else
        {
            var form = await req.ReadFormAsync();
            app.Logger.LogInformation("POST /api/players/v1/create content-type=" + contentType + " formKeys=" + string.Join(",", form.Keys));
            int.TryParse(form["Platform"], out platform);
            long.TryParse(form["PlatformId"], out platformId);
            name = form["Name"].ToString();
            if (string.IsNullOrEmpty(name))
            {
                int.TryParse(form["platform"], out platform);
                long.TryParse(form["platformId"], out platformId);
                name = form["name"].ToString();
            }
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError("POST /api/players/v1/create failed to read request: " + ex.Message);
    }
    if (string.IsNullOrEmpty(name))
    {
        name = "IPhone 4 running IOS 6";
    }
    app.Logger.LogInformation("POST /api/players/v1/create platform=" + platform + " platformId=" + platformId + " name=" + name);
    Execute("INSERT OR IGNORE INTO players (Platform, PlatformId, Name) VALUES ($p, $pid, $name)", p =>
    {
        p.AddWithValue("$p", platform);
        p.AddWithValue("$pid", platformId);
        p.AddWithValue("$name", name);
    });
    var created = ToNovProfile(FindPlayerByPlatform(platform, platformId));
    app.Logger.LogInformation("POST /api/players/v1/create response=" + JsonSerializer.Serialize(created));
    return Results.Json(created);
});

app.MapPost("/api/players/v1/update/{id:long}", async (long id, HttpRequest req) =>
{
    Dictionary<string, object> profile = FindPlayerById(id);
    if (profile.Count == 0)
    {
        return Results.Json(new Dictionary<string, object>());
    }
    string body;
    using (var reader = new StreamReader(req.Body))
    {
        body = await reader.ReadToEndAsync();
    }
    try
    {
        var patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        if (patch != null)
        {
            string newName = "";
            if (patch.TryGetValue("Username", out var username) && username.ValueKind == JsonValueKind.String)
            {
                newName = username.GetString() ?? "";
            }
            if (patch.TryGetValue("DisplayName", out var display) && display.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(newName))
            {
                newName = display.GetString() ?? "";
            }
            if (patch.TryGetValue("Name", out var name) && name.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(newName))
            {
                newName = name.GetString() ?? "";
            }
            if (!string.IsNullOrEmpty(newName))
            {
                profile["Name"] = newName;
            }
            foreach (string key in new string[] { "XP", "Level", "Reputation", "Verified" })
            {
                if (patch.TryGetValue(key, out var el))
                {
                    if (el.ValueKind == JsonValueKind.Number)
                    {
                        profile[key] = el.GetInt32();
                    }
                    else if (el.ValueKind == JsonValueKind.True)
                    {
                        profile[key] = 1;
                    }
                    else if (el.ValueKind == JsonValueKind.False)
                    {
                        profile[key] = 0;
                    }
                }
            }
        }
    }
    catch { }
    Execute("UPDATE players SET Name = $name, XP = $xp, Level = $level, Reputation = $rep, Verified = $ver WHERE Id = $id", p =>
    {
        p.AddWithValue("$name", profile["Name"]);
        p.AddWithValue("$xp", profile["XP"]);
        p.AddWithValue("$level", profile["Level"]);
        p.AddWithValue("$rep", profile["Reputation"]);
        p.AddWithValue("$ver", profile["Verified"]);
        p.AddWithValue("$id", id);
    });
    return Results.Json(ToNovProfile(profile));
});

app.MapPost("/api/players/v1/verify/{id:long}", async (long id, HttpRequest req) =>
{
    string email = "";
    try
    {
        var form = await req.ReadFormAsync();
        email = form["email"].ToString();
    }
    catch
    {
    }
    if (string.IsNullOrEmpty(email))
    {
        email = "unknown@bassment.local";
    }
    Execute("UPDATE players SET Verified = 1, Email = $email WHERE Id = $id", p =>
    {
        p.AddWithValue("$email", email);
        p.AddWithValue("$id", id);
    });
    app.Logger.LogInformation("POST /api/players/v1/verify/" + id + " email=" + email + " -> verified");
    return Results.Json(new Dictionary<string, object> { ["Message"] = "Account verified" });
});

app.MapGet("/api/config/v1/objectives", () =>
{
    int[] pool = new int[] { 100, 201, 301, 400, 500, 801, 802 };
    List<object> week = new List<object>();
    for (int day = 0; day < 7; day++)
    {
        List<object> date = new List<object>();
        for (int slot = 0; slot < 3; slot++)
        {
            date.Add(new Dictionary<string, object>
            {
                ["type"] = pool[(day + slot * 2) % pool.Length],
                ["score"] = slot + 1,
                ["xp"] = 100
            });
        }
        week.Add(date);
    }
    return Results.Json(week);
});

app.MapGet("/api/avatar/v1/items/{playerId:long}", (long playerId) =>
{
    return Results.Json(Query("SELECT AvatarItemDesc FROM avatar_items WHERE PlayerId = $id", r =>
    {
        List<object> items = new List<object>();
        do
        {
            items.Add(r.GetString(0));
        } while (r.Read());
        return items;
    }, p => p.AddWithValue("$id", playerId)) ?? new List<object>());
});

app.MapPost("/api/avatar/v1/items/create", async (HttpRequest req) =>
{
    try
    {
        var form = await req.ReadFormAsync();
        long playerId = long.TryParse(form["PlayerId"], out var pid) ? pid : 0;
        string desc = form["AvatarItemDesc"].ToString();
        if (playerId != 0 && !string.IsNullOrEmpty(desc))
        {
            Execute("INSERT INTO avatar_items (PlayerId, AvatarItemDesc) VALUES ($id, $desc)", p =>
            {
                p.AddWithValue("$id", playerId);
                p.AddWithValue("$desc", desc);
            });
        }
    }
    catch { }
    return Results.Ok();
});

app.MapGet("/api/avatar/v1/{id:long}", (long id) =>
{
    Dictionary<string, object> avatar = Query("SELECT OutfitSelections, SkinColor, HairColor FROM avatars WHERE PlayerId = $id", r => new Dictionary<string, object>
    {
        ["OutfitSelections"] = r.GetString(0),
        ["SkinColor"] = r.GetString(1),
        ["HairColor"] = r.GetString(2)
    }, p => p.AddWithValue("$id", id)) ?? new Dictionary<string, object>
    {
        ["OutfitSelections"] = "",
        ["SkinColor"] = "",
        ["HairColor"] = ""
    };
    return Results.Json(avatar);
});

app.MapPost("/api/avatar/v1/set", async (HttpRequest req) =>
{
    try
    {
        string body;
        using (var reader = new StreamReader(req.Body))
        {
            body = await reader.ReadToEndAsync();
        }
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        if (doc != null)
        {
            long playerId = 0;
            string outfit = "";
            string skin = "";
            string hair = "";
            if (doc.TryGetValue("PlayerId", out var pid) && pid.ValueKind == JsonValueKind.Number)
            {
                playerId = pid.GetInt64();
            }
            if (doc.TryGetValue("OutfitSelections", out var o) && o.ValueKind == JsonValueKind.String)
            {
                outfit = o.GetString() ?? "";
            }
            if (doc.TryGetValue("SkinColor", out var s) && s.ValueKind == JsonValueKind.String)
            {
                skin = s.GetString() ?? "";
            }
            if (doc.TryGetValue("HairColor", out var h) && h.ValueKind == JsonValueKind.String)
            {
                hair = h.GetString() ?? "";
            }
            if (playerId != 0)
            {
                Execute("INSERT OR REPLACE INTO avatars (PlayerId, OutfitSelections, SkinColor, HairColor) VALUES ($id, $o, $s, $h)", p =>
                {
                    p.AddWithValue("$id", playerId);
                    p.AddWithValue("$o", outfit);
                    p.AddWithValue("$s", skin);
                    p.AddWithValue("$h", hair);
                });
            }
        }
    }
    catch { }
    return Results.Ok();
});

app.MapGet("/api/settings/v1/{id:long}", (long id) =>
{
    return Results.Json(Query("SELECT Key, Value FROM settings WHERE PlayerId = $id", r =>
    {
        List<object> prefs = new List<object>();
        do
        {
            prefs.Add(new Dictionary<string, object> { ["Key"] = r.GetString(0), ["Value"] = r.GetString(1) });
        } while (r.Read());
        return prefs;
    }, p => p.AddWithValue("$id", id)) ?? new List<object>());
});

app.MapPost("/api/settings/v1/set", async (HttpRequest req) =>
{
    try
    {
        var form = await req.ReadFormAsync();
        long playerId = long.TryParse(form["PlayerId"], out var pid) ? pid : 0;
        string key = form["Key"].ToString();
        string value = form["Value"].ToString();
        if (playerId != 0 && !string.IsNullOrEmpty(key))
        {
            Execute("INSERT OR REPLACE INTO settings (PlayerId, Key, Value) VALUES ($id, $k, $v)", p =>
            {
                p.AddWithValue("$id", playerId);
                p.AddWithValue("$k", key);
                p.AddWithValue("$v", value);
            });
        }
    }
    catch { }
    return Results.Ok();
});

app.MapPost("/api/settings/v1/remove", async (HttpRequest req) =>
{
    try
    {
        var form = await req.ReadFormAsync();
        long playerId = long.TryParse(form["PlayerId"], out var pid) ? pid : 0;
        string key = form["Key"].ToString();
        if (playerId != 0 && !string.IsNullOrEmpty(key))
        {
            Execute("DELETE FROM settings WHERE PlayerId = $id AND Key = $k", p =>
            {
                p.AddWithValue("$id", playerId);
                p.AddWithValue("$k", key);
            });
        }
    }
    catch { }
    return Results.Ok();
});

app.MapGet("/api/tournament", (HttpRequest req) =>
{
    return Results.Json(Array.Empty<object>());
});

app.MapGet("/api/tournament/forfeit", (HttpRequest req) =>
{
    return Results.Json(Array.Empty<object>());
});

app.MapGet("/api/images/v1/profile/{id:long}", (long id) =>
{
    byte[] image = Query("SELECT Data FROM images WHERE PlayerId = $id", r => (byte[])r["Data"], p => p.AddWithValue("$id", id)) ?? Array.Empty<byte>();
    return Results.Bytes(image, "application/octet-stream");
});

app.MapPost("/api/images/v1/profile/{id:long}", async (long id, HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    if (form.Files.Count > 0)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            await form.Files[0].CopyToAsync(ms);
            data = ms.ToArray();
        }
        Execute("INSERT OR REPLACE INTO images (PlayerId, Data) VALUES ($id, $data)", p =>
        {
            p.AddWithValue("$id", id);
            p.AddWithValue("$data", data);
        });
    }
    return Results.Ok();
});

app.Run();
