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
    public int IconBaseId { get; set; } = 0;
    public bool ShowBorder { get; set; } = false;
    public Vector4 BorderColor { get; set; } = new(1.00f, 1.00f, 1.00f, 0.90f);
    public bool ShowFill { get; set; } = false;
    public Vector4 FillColor { get; set; } = new(1.00f, 1.00f, 1.00f, 0.40f);
    public bool ClipToCircle { get; set; } = false;
    public float SizeMultiplier { get; set; } = 1.0f;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool HasCompletedFirstTimeSetup { get; set; } = false;

    public float CompassWidth { get; set; } = 570f;
    public float CompassHeight { get; set; } = 35f;
    public float YOffset { get; set; } = 10f;
    public float XOffset { get; set; } = 0f;
    public bool ShowCompassBar { get; set; } = true;

    public float VisibleDegrees { get; set; } = 90f;
    public float LensStrength { get; set; } = 2.0f;
    public float RotationOffset { get; set; } = 0f;
    public bool UseCameraDirection { get; set; } = true;
    public bool UseCameraPosition { get; set; } = true;
    public float FontScale { get; set; } = 1.0f;
    public bool HideDuringCutscenes { get; set; } = true;

    public Vector4 BackgroundColor { get; set; } = new(0.05f, 0.04f, 0.03f, 0.82f);
    public Vector4 BorderColor { get; set; } = new(0.48f, 0.42f, 0.27f, 0.92f);
    public Vector4 CardinalColor { get; set; } = new(1.00f, 0.97f, 0.88f, 1.00f);
    public Vector4 IntercardinalColor { get; set; } = new(0.72f, 0.70f, 0.65f, 0.88f);
    public Vector4 TickColor { get; set; } = new(0.58f, 0.56f, 0.52f, 0.72f);

    public bool ShowPlayers { get; set; } = true;
    public bool SolidFriendDots { get; set; } = true;
    public bool ShowPartyRoleIcons { get; set; } = true;
    public bool PartyRoleIconsOnlyInDuty { get; set; } = true;
    public float PartyRoleIconMinSize { get; set; } = 8f;
    public float PartyRoleIconMaxSize { get; set; } = 25f;

    public List<PlayerIconOverride> PlayerIconOverrides { get; set; } = new();
    public int PlayerIconOverridesVersion { get; set; } = 0;
    public void IncrementOverrideVersion() => PlayerIconOverridesVersion++;

    public bool ShowEnemies { get; set; } = true;
    public bool EnemiesOnlyIfEngaged { get; set; } = true;
    public float EnemyMinSize { get; set; } = 8f;
    public float EnemyMaxSize { get; set; } = 25f;

    public bool ShowLimitBreakGlow { get; set; } = true;
    public Vector4 LimitBreakGlowColor { get; set; } = new(1.00f, 0.65f, 0.10f, 0.95f);
    public Vector4 LimitBreakGlowColor2 { get; set; } = new(1.00f, 0.95f, 0.20f, 0.95f);
    public Vector4 LimitBreakGlowColor3 { get; set; } = new(1.00f, 1.00f, 1.00f, 0.95f);

    public bool ShowTargetBar { get; set; } = true;
    public float TargetBarWidthFraction { get; set; } = 0.925f;
    public float TargetBarHeight { get; set; } = 12f;
    public float TargetBarFontScale { get; set; } = 1.0f;
    public bool ShowTargetLevel { get; set; } = true;
    public bool ShowTargetBarShield { get; set; } = true;
    public bool ShowTargetHealthPercent { get; set; } = false;
    public bool ShowTargetOfTargetHealthPercent { get; set; } = false;
    public bool ShowTargetBarRibbons { get; set; } = true;
    public Vector4 TargetBarShieldColor { get; set; } = new(0.80f, 0.92f, 1.00f, 0.55f);

    public bool ShowTargetStatuses { get; set; } = true;
    public float TargetStatusIconSize { get; set; } = 25f;
    public int TargetStatusMaxIcons { get; set; } = 10;

    public bool MirrorMoodlesLoci { get; set; } = true;

    public bool ShowTargetOfTargetBar { get; set; } = true;
    public bool HighlightIfTargetingMe { get; set; } = true;
    public Vector4 AggroWarningColor { get; set; } = new(1.00f, 0.82f, 0.16f, 1.00f);
    public bool ShowTargetOfTargetName { get; set; } = true;
    public bool TargetOfTargetFirstNameOnly { get; set; } = false;
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
    public Vector4 AetheryteColor { get; set; } = new(0.55f, 0.85f, 0.95f, 0.92f);
    public float AetheryteIconMinSize { get; set; } = 8f;
    public float AetheryteIconMaxSize { get; set; } = 30f;

    public float MaxMarkerDistance { get; set; } = 100f;
    public float DotNearZone { get; set; } = 0.925f;
    public float DotFarZone { get; set; } = 0.325f;
    public float DotMidAlpha { get; set; } = 0.325f;

    public Vector4 PlayerColor { get; set; } = new(0.40f, 0.65f, 1.00f, 0.92f);
    public Vector4 EnemyColor { get; set; } = new(1.00f, 0.25f, 0.25f, 0.92f);
    public Vector4 NpcColor { get; set; } = new(0.95f, 0.88f, 0.35f, 0.92f);
    public Vector4 GatheringColor { get; set; } = new(0.30f, 0.92f, 0.40f, 0.92f);
    public Vector4 TreasureColor { get; set; } = new(1.00f, 0.80f, 0.15f, 0.95f);

    public bool ShowFates { get; set; } = true;
    public Vector4 FateColor { get; set; } = new(0.82f, 0.35f, 0.95f, 0.95f);
    public float FateDistanceMultiplier { get; set; } = 3.5f;
    public float FateIconMinSize { get; set; } = 20f;
    public float FateIconMaxSize { get; set; } = 40f;

    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}