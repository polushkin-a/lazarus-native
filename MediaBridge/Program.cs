// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Text;
using System.Text.Json.Nodes;
using MediaBridge;

// MediaBridge — what Windows is playing and how loud, for Lazarus' Media plugin (docs/plugins/MEDIA.md in Lazarus).
// Reads the system media sessions (the player Windows shows in its media flyout: Spotify, a browser tab, VLC…) and the
// default output's volume, and drives them. Talks JSON lines: events out on stdout, commands in on stdin; exits when
// stdin closes (its plugin is gone). No elevation, no network.

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
var gate = new object();
void Emit(JsonObject o)
{
    lock (gate)
    {
        Console.Out.WriteLine(o.ToJsonString());
        Console.Out.Flush();
    }
}
void Error(string what, Exception e) => Emit(new JsonObject { ["t"] = "error", ["message"] = $"{what}: {e.Message}" });

var media = new MediaWatch(Emit);
var volume = new VolumeWatch(Emit);
try { await media.StartAsync(); } catch (Exception e) { Error("media sessions", e); }
try { volume.Start(); } catch (Exception e) { Error("volume", e); }

string? line;
while ((line = await Console.In.ReadLineAsync()) != null)
{
    try
    {
        if (JsonNode.Parse(line) is not JsonObject cmd) continue;
        var c = cmd["c"]?.GetValue<string>() ?? "";
        switch (c)
        {
            case "playpause" or "next" or "prev" or "stop":
                await media.CommandAsync(c);
                break;
            case "seek":
                await media.SeekAsync(cmd["s"]?.GetValue<double>() ?? 0);
                break;
            case "volume":
                volume.Set(cmd["v"]?.GetValue<double>() ?? 0);
                break;
            case "mute":
                volume.ToggleMute();
                break;
        }
    }
    catch (Exception e)
    {
        Error("command", e);
    }
}
volume.Dispose();
