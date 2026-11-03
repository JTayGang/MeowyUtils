using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

public enum LanguageSetting { Auto, English, Japanese }

[Serializable]
public class PlayerIconOverride
{
    public string PlayerName = "";
    public int IconBaseId;
    public bool ShowBorder;
    public Vector4 BorderColor = new(1, 1, 1, .9f);
    public bool ShowFill;
    public Vector4 FillColor = new(1, 1, 1, .4f);
    public bool ClipToCircle;
    public float SizeMultiplier = 1f;
}

// Public fields (not properties) so the settings window can bind them with ref.
[Serializable]
public class Configuration : IPluginConfiguration
{
    public const float DefaultYOffset = 10f;

    public int Version { get; set; } = 1;
    public bool Enabled = true;
    public bool HasCompletedFirstTimeSetup;
    public LanguageSetting Language = LanguageSetting.Auto;

    public float CompassWidth = 570f;
    public float CompassHeight = 35f;
    public float YOffset = DefaultYOffset;
    public float XOffset;
    public bool LockPosition = true;
    public bool ShowCompassBar = true;

    public float VisibleDegrees = 90f;
    public float LensStrength = 2f;
    public float RotationOffset;
    public bool UseCameraDirection = true;
    public bool UseCameraPosition = true;
    public float FontScale = 1f;
    public bool HideDuringCutscenes = true;

    public Vector4 BackgroundColor = new(.05f, .04f, .03f, .82f);
    public Vector4 BorderColor = new(.48f, .42f, .27f, .92f);
    public Vector4 CardinalColor = new(1f, .97f, .88f, 1f);
    public Vector4 IntercardinalColor = new(.72f, .70f, .65f, .88f);
    public Vector4 TickColor = new(.58f, .56f, .52f, .72f);

    public bool ShowPlayers = true;
    public bool SolidFriendDots = true;
    public bool ShowPartyRoleIcons = true;
    public bool PartyRoleIconsOnlyInDuty = true;
    public float PartyRoleIconMinSize = 8f;
    public float PartyRoleIconMaxSize = 25f;

    public List<PlayerIconOverride> PlayerIconOverrides = new();
    public int PlayerIconOverridesVersion;
    public void IncrementOverrideVersion() => PlayerIconOverridesVersion++;

    public bool ShowEnemies = true;
    public bool EnemiesOnlyIfEngaged = true;
    public float EnemyMinSize = 8f;
    public float EnemyMaxSize = 25f;

    public bool ShowLimitBreakGlow = true;
    public Vector4 LimitBreakGlowColor = new(1f, .65f, .10f, .95f);
    public Vector4 LimitBreakGlowColor2 = new(1f, .95f, .20f, .95f);
    public Vector4 LimitBreakGlowColor3 = new(1f, 1f, 1f, .95f);

    public bool ShowTargetBar = true;
    public float TargetBarWidthFraction = .925f;
    public float TargetBarHeight = 12f;
    public float TargetBarFontScale = 1f;
    public bool ShowTargetLevel = true;
    public bool ShowTargetBarShield = true;
    public bool ShowTargetHealthPercent;
    public bool ShowTargetOfTargetHealthPercent;
    public bool ShowTargetBarRibbons = true;
    public Vector4 TargetBarShieldColor = new(.80f, .92f, 1f, .55f);

    public bool ShowTargetStatuses = true;
    public float TargetStatusIconSize = 25f;
    public int TargetStatusMaxIcons = 10;
    public bool TargetStatusIconAlignLeft;
    public bool TargetStatusIconAlignRight;
    public bool ShowTargetStatusEsunaMarker = true;
    public Vector4 TargetStatusEsunaMarkerColor = new(.71f, .82f, .88f, .95f);

    public bool MirrorMoodlesLoci = true;

    public bool ShowTargetOfTargetBar = true;
    public bool HighlightIfTargetingMe = true;
    public Vector4 AggroWarningColor = new(1f, .82f, .16f, 1f);
    public bool ShowTargetOfTargetName = true;
    public bool TargetOfTargetFirstNameOnly;
    public bool TargetOfTargetShowYou = true;

    public bool ShowNpcs = true;
    public bool NpcsOnlyIfTargetable = true;
    public bool ShowNpcQuestIcons = true;
    public float NpcQuestIconMinSize = 8f;
    public float NpcQuestIconMaxSize = 40f;

    public int MenderIconId = 60434;
    public bool ShowShopIcons = true;
    public int ShopIconId = 60412;
    public bool ShowFastTravelIcons = true;
    public int FastTravelIconId = 60456;
    public int FastTravelTicketerIconId = 60352;
    public int ChocoboKeepIconId = 60311;

    public bool ShowGatheringNodes = true;
    public bool GatheringOnlyIfTargetable = true;
    public bool ShowGatheringIcons = true;
    public float GatheringIconMinSize = 8f;
    public float GatheringIconMaxSize = 60f;

    public bool ShowTreasure = true;
    public bool ShowTreasureIcons = true;
    public int TreasureIconId = 60354;
    public float TreasureMinSize = 8f;
    public float TreasureMaxSize = 60f;

    public bool ShowAetherytes = true;
    public bool ShowAethernetShards = true;
    public bool ShowAetheryteIcons = true;
    public int AetheryteIconId = 60453;
    public int AethernetShardIconId = 60430;
    public Vector4 AetheryteColor = new(.55f, .85f, .95f, .92f);
    public float AetheryteIconMinSize = 8f;
    public float AetheryteIconMaxSize = 30f;

    public float MaxMarkerDistance = 100f;
    public float DotNearZone = .925f;
    public float DotFarZone = .325f;
    public float DotMidAlpha = .325f;

    public Vector4 PlayerColor = new(.40f, .65f, 1f, .92f);
    public Vector4 EnemyColor = new(1f, .25f, .25f, .92f);
    public Vector4 NpcColor = new(.95f, .88f, .35f, .92f);
    public Vector4 GatheringColor = new(.30f, .92f, .40f, .92f);
    public Vector4 TreasureColor = new(1f, .80f, .15f, .95f);

    public bool ShowFates = true;
    public Vector4 FateColor = new(.82f, .35f, .95f, .95f);
    public float FateDistanceMultiplier = 3.5f;
    public float FateIconMinSize = 20f;
    public float FateIconMaxSize = 40f;

    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);

    private static readonly HashSet<string> KeepOnReset =
    [
        nameof(Enabled), nameof(HasCompletedFirstTimeSetup), nameof(Language), nameof(MirrorMoodlesLoci),
        nameof(PlayerIconOverrides), nameof(PlayerIconOverridesVersion),
    ];

    public void ResetToDefaults()
    {
        var fresh = new Configuration();
        foreach (var f in typeof(Configuration).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!KeepOnReset.Contains(f.Name)) f.SetValue(this, f.GetValue(fresh));
    }

    // Keeps hand-edited or corrupt values from breaking the math.
    public void Sanitize()
    {
        static void Fix(ref float v, float lo, float hi) => v = float.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;
        Fix(ref CompassWidth, 200f, 1400f);
        Fix(ref CompassHeight, 20f, 80f);
        Fix(ref VisibleDegrees, 30f, 180f);
        Fix(ref LensStrength, 1f, 3f);
        Fix(ref FontScale, .5f, 2.5f);
        Fix(ref TargetBarFontScale, .5f, 2.5f);
        Fix(ref TargetBarWidthFraction, .1f, 1f);
        Fix(ref TargetBarHeight, 4f, 40f);
        Fix(ref TargetStatusIconSize, 8f, 60f);
        Fix(ref MaxMarkerDistance, 10f, 500f);
        Fix(ref FateDistanceMultiplier, .5f, 5f);
        Fix(ref DotNearZone, .5f, 1f);
        Fix(ref DotFarZone, 0f, .5f);
        Fix(ref DotMidAlpha, 0f, 1f);
        Fix(ref XOffset, -4000f, 4000f);
        Fix(ref YOffset, 0f, 4000f);
        Fix(ref RotationOffset, -180f, 180f);
        TargetStatusMaxIcons = Math.Clamp(TargetStatusMaxIcons, 1, 30);
        PlayerIconOverrides ??= new();
    }
}
