using System;
using HarmonyLib;
using P2.Game.Map.Weather;
using PataCoop.Net;
using UnityEngine;

namespace PataCoop.Coop;

/// <summary>
/// The host's game is the one source of truth for the shared world: this class carries the
/// parts of it that each machine would otherwise decide on its own.
///
/// Weather: a mission's weather comes from the player's own save (the world map's weather flags)
/// and from the mission script, so two players can see rain and sunshine in the same battle.
/// The host sends its weather whenever it changes; the other machines ignore their own weather
/// changes during a co-op battle and take the host's.
/// </summary>
internal static class WorldSync
{
    /// <summary>The weather values of one moment (the controller's current parameters).</summary>
    private sealed class Weather
    {
        public uint Kind, Effects;
        public int Rain, Wind, Cloud, Thunder, Snow, Fog, Sand;
        public float DirX, DirY;

        public static Weather? Read(P2.Game.Game? game)
        {
            var p = game?.map_?.weatherController_?.currentParam_;
            if (p == null) return null;
            return new Weather
            {
                Kind = p.currentWeather, Effects = p.applyEffects, Rain = p.rainyLevel, Wind = p.windLevel, Cloud = p.cloudyLevel,
                Thunder = p.thunderLevel, Snow = p.snowLevel, Fog = p.fogLevel, Sand = p.sandLevel, DirX = p.windDir.x, DirY = p.windDir.y,
            };
        }

        public bool SameAs(Weather? o) => o != null && Kind == o.Kind && Rain == o.Rain && Wind == o.Wind && Cloud == o.Cloud && Thunder == o.Thunder
            && Snow == o.Snow && Fog == o.Fog && Sand == o.Sand && Math.Abs(DirX - o.DirX) < 0.01f && Math.Abs(DirY - o.DirY) < 0.01f;

        public override string ToString() => $"weather {Kind} rain {Rain} wind {Wind} ({DirX:F1}) cloud {Cloud} thunder {Thunder} snow {Snow} fog {Fog} sand {Sand}";
    }

    /// <summary>True while we apply the host's weather (the only weather change a guest lets through).</summary>
    internal static bool ApplyingWeather;

    private static Weather? _sent, _hostWeather;
    private static int _nextCheck, _appliedFrame = -1000;

    /// <summary>Our battle has started: the host sends its weather afresh.</summary>
    internal static void BattleStarted() => _sent = null;

    /// <summary>The battle is over: forget its weather (the host's word may arrive before our own battle starts, so not earlier).</summary>
    internal static void Reset()
    {
        _sent = null;
        _hostWeather = null;
        _appliedFrame = -1000;
    }

    internal static void Tick()
    {
        if (!Battle.Active || Time.frameCount < _nextCheck) return;
        _nextCheck = Time.frameCount + 15;
        var game = P2.Game.Game.pGame_g;
        if (CoopNet.IsHost)
        {
            var now = Weather.Read(game);
            if (now == null || now.SameAs(_sent)) return;
            _sent = now;
            CoopNet.SendAll(new MsgWriter(Msg.Weather).U32(now.Kind).I32(now.Rain).I32(now.Wind).I32(now.Cloud).I32(now.Thunder)
                .I32(now.Snow).I32(now.Fog).I32(now.Sand).F32(now.DirX).F32(now.DirY).U32(now.Effects).ToArray(), true);
        }
        else if (_hostWeather != null && Time.frameCount - _appliedFrame > 120 && !_hostWeather.SameAs(Weather.Read(game)))
        {
            Apply(game, _hostWeather); // first time, or the controller was rebuilt: take the host's again
        }
    }

    internal static void OnWeather(int fromSlot, MsgReader r)
    {
        if (fromSlot != 0 || CoopNet.IsHost) return;
        _hostWeather = new Weather
        {
            Kind = r.U32(), Rain = r.I32(), Wind = r.I32(), Cloud = r.I32(), Thunder = r.I32(), Snow = r.I32(), Fog = r.I32(), Sand = r.I32(),
            DirX = r.F32(), DirY = r.F32(), Effects = r.U32(),
        };
        if (Battle.Active) Apply(P2.Game.Game.pGame_g, _hostWeather);
    }

    private static void Apply(P2.Game.Game? game, Weather w)
    {
        var wc = game?.map_?.weatherController_;
        if (wc == null) return;
        const ParamId id = ParamId.ParamId_Default;
        const float fade = 1f;
        ApplyingWeather = true;
        try
        {
            var dir = new Vector2(w.DirX, w.DirY);
            wc.setRainyLevel((uint)Math.Max(0, w.Rain), id);
            wc.setWindLevel((uint)Math.Max(0, w.Wind), id);
            wc.setWindDirection(ref dir, id);
            wc.setCloudyLevel((uint)Math.Max(0, w.Cloud), id);
            wc.setThunderLevel((uint)Math.Max(0, w.Thunder), id);
            wc.setSnowLevel((uint)Math.Max(0, w.Snow), id);
            wc.setFogLevel((uint)Math.Max(0, w.Fog), id);
            wc.setSandLevel((uint)Math.Max(0, w.Sand), id);
            wc.changeWeather(w.Kind, fade, id);
            wc.applyParam(id, fade, w.Effects);
            _appliedFrame = Time.frameCount;
            CoopPlugin.L.LogInfo("[coop] host " + w);
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogWarning("could not apply the host's weather: " + e.Message);
        }
        finally
        {
            ApplyingWeather = false;
        }
    }
}

/// <summary>On a guest, weather changes from its own save, scripts or miracles wait for the host's word.</summary>
[HarmonyPatch]
internal static class GuestWeatherPatch
{
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "changeWeather", "setWindLevel", "setWindDirection", "setRainyLevel", "setCloudyLevel",
                     "setThunderLevel", "setSnowLevel", "setFogLevel", "setSandLevel", "applyParam" })
            yield return AccessTools.Method(typeof(Controller), name);
    }

    private static bool Prefix() => !Battle.Active || CoopNet.IsHost || WorldSync.ApplyingWeather;
}
