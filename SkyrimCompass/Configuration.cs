using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

[Serializable]
public class PlayerIconOverride
{
    public string PlayerName { get; set; } = "";
    public int IconBaseId { get; set; }
    public bool ShowBorder { get; set; }
    public Vector4 BorderColor { get; set; } = new(1,1,1,.9f);
    public bool ShowFill { get; set; }
    public Vector4 FillColor { get; set; } = new(1,1,1,.4f);
    public bool ClipToCircle { get; set; }
    public float SizeMultiplier { get; set; } = 1f;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool HasCompletedFirstTimeSetup { get; set; }

    public float CompassWidth { get; set; } = 570f;
    public float CompassHeight { get; set; } = 35f;
    public float YOffset { get; set; } = 10f;
    public float XOffset { get; set; }
    public bool LockPosition { get; set; } = true;
    public bool ShowCompassBar { get; set; } = true;

    public float VisibleDegrees { get; set; } = 90f;
    public float LensStrength { get; set; } = 2f;
    public float RotationOffset { get; set; }
    public bool UseCameraDirection { get; set; } = true;
    public bool UseCameraPosition { get; set; } = true;
    public float FontScale { get; set; } = 1f;
    public bool HideDuringCutscenes { get; set; } = true;

    public Vector4 BackgroundColor { get; set; } = new(.05f,.04f,.03f,.82f);
    public Vector4 BorderColor { get; set; } = new(.48f,.42f,.27f,.92f);
    public Vector4 CardinalColor { get; set; } = new(1f,.97f,.88f,1f);
    public Vector4 IntercardinalColor { get; set; } = new(.72f,.70f,.65f,.88f);
    public Vector4 TickColor { get; set; } = new(.58f,.56f,.52f,.72f);

    public bool ShowPlayers { get; set; } = true;
    public bool SolidFriendDots { get; set; } = true;
    public bool ShowPartyRoleIcons { get; set; } = true;
    public bool PartyRoleIconsOnlyInDuty { get; set; } = true;
    public float PartyRoleIconMinSize { get; set; } = 8f;
    public float PartyRoleIconMaxSize { get; set; } = 25f;

    public List<PlayerIconOverride> PlayerIconOverrides { get; set; } = new();
    public int PlayerIconOverridesVersion { get; set; }
    public void IncrementOverrideVersion() => PlayerIconOverridesVersion++;

    public bool ShowEnemies { get; set; } = true;
    public bool EnemiesOnlyIfEngaged { get; set; } = true;
    public float EnemyMinSize { get; set; } = 8f;
    public float EnemyMaxSize { get; set; } = 25f;

    public bool ShowLimitBreakGlow { get; set; } = true;
    public Vector4 LimitBreakGlowColor { get; set; } = new(1f,.65f,.10f,.95f);
    public Vector4 LimitBreakGlowColor2 { get; set; } = new(1f,.95f,.20f,.95f);
    public Vector4 LimitBreakGlowColor3 { get; set; } = new(1f,1f,1f,.95f);

    public bool ShowTargetBar { get; set; } = true;
    public float TargetBarWidthFraction { get; set; } = .925f;
    public float TargetBarHeight { get; set; } = 12f;
    public float TargetBarFontScale { get; set; } = 1f;
    public bool ShowTargetLevel { get; set; } = true;
    public bool ShowTargetBarShield { get; set; } = true;
    public bool ShowTargetHealthPercent { get; set; }
    public bool ShowTargetOfTargetHealthPercent { get; set; }
    public bool ShowTargetBarRibbons { get; set; } = true;
    public Vector4 TargetBarShieldColor { get; set; } = new(.80f,.92f,1f,.55f);

    public bool ShowTargetStatuses { get; set; } = true;
    public float TargetStatusIconSize { get; set; } = 25f;
    public int TargetStatusMaxIcons { get; set; } = 10;
    public bool TargetStatusIconAlignLeft { get; set; }
    public bool TargetStatusIconAlignRight { get; set; }

    public bool MirrorMoodlesLoci { get; set; } = true;

    public bool ShowTargetOfTargetBar { get; set; } = true;
    public bool HighlightIfTargetingMe { get; set; } = true;
    public Vector4 AggroWarningColor { get; set; } = new(1f,.82f,.16f,1f);
    public bool ShowTargetOfTargetName { get; set; } = true;
    public bool TargetOfTargetFirstNameOnly { get; set; }
    public bool TargetOfTargetShowYou { get; set; } = true;

    public bool ShowNpcs { get; set; } = true;
    public bool NpcsOnlyIfTargetable { get; set; } = true;
    public bool ShowNpcQuestIcons { get; set; } = true;
    public float NpcQuestIconMinSize { get; set; } = 8f;
    public float NpcQuestIconMaxSize { get; set; } = 40f;

    public int MenderIconId { get; set; } = 60434;
    public bool ShowShopIcons { get; set; } = true;
    public int ShopIconId { get; set; } = 60412;
    public bool ShowFastTravelIcons { get; set; } = true;
    public int FastTravelIconId { get; set; } = 60456;
    public int FastTravelTicketerIconId { get; set; } = 60352;
    public int ChocoboKeepIconId { get; set; } = 60311;

    public bool ShowGatheringNodes { get; set; } = true;
    public bool GatheringOnlyIfTargetable { get; set; } = true;
    public bool ShowGatheringIcons { get; set; } = true;
    public float GatheringIconMinSize { get; set; } = 8f;
    public float GatheringIconMaxSize { get; set; } = 60f;

    public bool ShowTreasure { get; set; } = true;
    public bool ShowTreasureIcons { get; set; } = true;
    public int TreasureIconId { get; set; } = 60354;
    public float TreasureMinSize { get; set; } = 8f;
    public float TreasureMaxSize { get; set; } = 60f;

    public bool ShowAetherytes { get; set; } = true;
    public bool ShowAethernetShards { get; set; } = true;
    public bool ShowAetheryteIcons { get; set; } = true;
    public string AethernetShardName { get; set; } = "Aethernet";
    public int AetheryteIconId { get; set; } = 60453;
    public int AethernetShardIconId { get; set; } = 60430;
    public Vector4 AetheryteColor { get; set; } = new(.55f,.85f,.95f,.92f);
    public float AetheryteIconMinSize { get; set; } = 8f;
    public float AetheryteIconMaxSize { get; set; } = 30f;

    public float MaxMarkerDistance { get; set; } = 100f;
    public float DotNearZone { get; set; } = .925f;
    public float DotFarZone { get; set; } = .325f;
    public float DotMidAlpha { get; set; } = .325f;

    public Vector4 PlayerColor { get; set; } = new(.40f,.65f,1f,.92f);
    public Vector4 EnemyColor { get; set; } = new(1f,.25f,.25f,.92f);
    public Vector4 NpcColor { get; set; } = new(.95f,.88f,.35f,.92f);
    public Vector4 GatheringColor { get; set; } = new(.30f,.92f,.40f,.92f);
    public Vector4 TreasureColor { get; set; } = new(1f,.80f,.15f,.95f);

    public bool ShowFates { get; set; } = true;
    public Vector4 FateColor { get; set; } = new(.82f,.35f,.95f,.95f);
    public float FateDistanceMultiplier { get; set; } = 3.5f;
    public float FateIconMinSize { get; set; } = 20f;
    public float FateIconMaxSize { get; set; } = 40f;

    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}