using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkyrimCompass;

// Per-player icon override — case-insensitive name match; falls back to a dot if the texture doesnt resolve
[Serializable]
public class PlayerIconOverride
{
    public string  PlayerName     { get; set; } = "";
    public int     IconBaseId     { get; set; } = 0;      // e.g. 62007=Paladin, 60453=Aetheryte, 61802=FC emblem
    public bool    ShowBorder     { get; set; } = false;
    public Vector4 BorderColor    { get; set; } = new(1.00f, 1.00f, 1.00f, 0.90f);
    public bool    ShowFill       { get; set; } = false;
    public Vector4 FillColor      { get; set; } = new(1.00f, 1.00f, 1.00f, 0.40f);
    public bool    ClipToCircle   { get; set; } = false;   // circular clip (AddImageRounded) vs square bounds
    public float   SizeMultiplier { get; set; } = 1.0f;    // extra scale on top of the global IconSizeMultiplier
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int  Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    // Layout
    public float CompassWidth  { get; set; } = 560f;
    public float CompassHeight { get; set; } = 35f;
    public float YOffset       { get; set; } = 8f;
    public float XOffset       { get; set; } = 0f;   // shifts bar left(-)/right(+) of horizontal center

    // Behaviour
    public float VisibleDegrees      { get; set; } = 90f;    // degrees of the full 360° visible at once
    public float LensStrength        { get; set; } = 2.0f;   // fisheye strength; 1.0=linear, 2.0≈+100% FOV at edges
    public float RotationOffset      { get; set; } = 0f;     // added to computed heading; set 180 if N/S swapped
    public bool  UseCameraDirection  { get; set; } = true;   // track camera yaw instead of character facing
    public bool  UseCameraPosition   { get; set; } = true;   // sub-option: bearings/distances from camera, not character
    public float FontScale           { get; set; } = 1.0f;
    public bool  ShowHeadingText     { get; set; } = false;
    public bool  HideDuringCutscenes { get; set; } = true;   // skips drawing during story/group-pose cutscenes

    // Colors
    public Vector4 BackgroundColor    { get; set; } = new(0.05f, 0.04f, 0.03f, 0.82f);
    public Vector4 BorderColor        { get; set; } = new(0.48f, 0.42f, 0.27f, 0.92f);
    public Vector4 CardinalColor      { get; set; } = new(1.00f, 0.97f, 0.88f, 1.00f);
    public Vector4 IntercardinalColor { get; set; } = new(0.72f, 0.70f, 0.65f, 0.88f);
    public Vector4 TickColor          { get; set; } = new(0.58f, 0.56f, 0.52f, 0.72f);

    // Marker toggles
    public bool  ShowPlayers        { get; set; } = true;
    public bool  SolidFriendDots    { get; set; } = true;   // friends render as solid dots (StatusFlags.Friend)
    public bool  ShowPartyRoleIcons { get; set; } = true;   // party job icon (ClassJob.RowId) + role-colored ring
    // Restricts the job icon/ring above to duty content + PvP (where role matters); elsewhere
    // members fall through to their named override, then the friend/hollow dot. Off = always-on
    public bool  PartyRoleIconsOnlyInDuty { get; set; } = true;
    public float PartyRoleIconMinSize     { get; set; } = 10f;
    public float PartyRoleIconMaxSize     { get; set; } = 24f;

    // Named player overrides — checked after party role icons (when shown, see
    // PartyRoleIconsOnlyInDuty), before the friend/ring fallback
    public List<PlayerIconOverride> PlayerIconOverrides { get; set; } = new();

    public bool  ShowEnemies          { get; set; } = true;
    public bool  EnemiesOnlyIfEngaged { get; set; } = true;   // only enemies targeting you or you're targeting
    public float EnemyMinSize         { get; set; } = 6f;
    public float EnemyMaxSize         { get; set; } = 20f;

    // Limit break glow — border glows in from both ends per bar's 0-100% progress; stacked
    // layers show charged bar count at a glance
    public bool    ShowLimitBreakGlow   { get; set; } = false;
    public Vector4 LimitBreakGlowColor  { get; set; } = new(1.00f, 0.65f, 0.10f, 0.95f);
    public Vector4 LimitBreakGlowColor2 { get; set; } = new(1.00f, 0.95f, 0.20f, 0.95f);   // bar 2, yellow by default
    public Vector4 LimitBreakGlowColor3 { get; set; } = new(1.00f, 1.00f, 1.00f, 0.95f);   // bar 3, white by default

    // Target health bar (Skyrim-style name+HP) — docked beneath the compass, reusing
    // Background/Border/Cardinal/IntercardinalColor above so the pair reads as one HUD column
    public bool    ShowTargetBar          { get; set; } = true;
    public float   TargetBarWidthFraction { get; set; } = 0.875f;   // fraction of CompassWidth
    public float   TargetBarHeight        { get; set; } = 14f;
    public float   TargetBarFontScale     { get; set; } = 1.0f;
    public bool    ShowTargetLevel        { get; set; } = true;
    public bool    ShowTargetBarShield    { get; set; } = true;   // sheen over ICharacter.ShieldPercentage
    public bool    ShowTargetBarRibbons   { get; set; } = true;   // glow ribbons flying out from the name's ornaments
    public Vector4 TargetBarHostileColor  { get; set; } = new(0.78f, 0.12f, 0.12f, 0.95f);
    public Vector4 TargetBarFriendlyColor { get; set; } = new(0.30f, 0.75f, 0.35f, 0.95f);   // everyone else, players+NPCs alike
    public Vector4 TargetBarShieldColor   { get; set; } = new(0.80f, 0.92f, 1.00f, 0.55f);

    // Target-of-target (FF14's ToT, restyled): auto-hidden if nobody/self, except targeting
    // YOU, which gets a dedicated warning color instead of hiding
    public bool    ShowTargetOfTargetBar  { get; set; } = true;
    public bool    HighlightIfTargetingMe { get; set; } = true;
    public Vector4 AggroWarningColor      { get; set; } = new(1.00f, 0.82f, 0.16f, 1.00f);

    public bool  ShowNpcs             { get; set; } = true;
    public bool  NpcsOnlyIfTargetable { get; set; } = true;   // hides non-targetable placeholders (e.g. empty stable slot)
    public bool  ShowNpcQuestIcons    { get; set; } = true;   // active-quest icon instead of a dot
    public float NpcQuestIconMinSize  { get; set; } = 8f;
    public float NpcQuestIconMaxSize  { get; set; } = 40f;

    // Mender/Shop/Fast-Travel below detect via ENpcResident's Title or Singular (vocation word),
    // always read in English regardless of client language. Share the NpcQuestIcon size range
    public bool ShowMenderIcons { get; set; } = true;
    public int  MenderIconId    { get; set; } = 60434;
    public bool ShowShopIcons   { get; set; } = true;
    public int  ShopIconId      { get; set; } = 60412;
    // Ferry skippers, airship/other ticketers, and Chocobo Keeps/Falcon Porters (Falcon
    // Porters share Chocobo Keep's keywords and icon) — one toggle, three icons
    public bool ShowFastTravelIcons      { get; set; } = true;
    public int  FastTravelIconId         { get; set; } = 60456;   // skippers
    public int  FastTravelTicketerIconId { get; set; } = 60352;   // ticketers
    public int  ChocoboKeepIconId        { get; set; } = 60311;   // Chocobo Keeps / Falcon Porters

    public bool  ShowGatheringNodes        { get; set; } = true;
    public bool  GatheringOnlyIfTargetable { get; set; } = true;   // hides non-targetable placeholders
    public bool  ShowGatheringIcons        { get; set; } = false;  // Mining/Botany/Quarrying/Logging icon
    public float GatheringIconMinSize      { get; set; } = 20f;
    public float GatheringIconMaxSize      { get; set; } = 30f;
    public bool  ShowTreasure              { get; set; } = true;
    public bool  ShowTreasureIcons         { get; set; } = true;   // no sheet maps BaseId->visual type, so one icon fits all
    public int   TreasureIconId            { get; set; } = 60354;  // 60354/60355/60356 are known variants — swap if wrong
    public float TreasureMinSize           { get; set; } = 6f;
    public float TreasureMaxSize           { get; set; } = 20f;
    public bool  ShowAetherytes            { get; set; } = true;
    public bool  ShowAethernetShards       { get; set; } = true;   // smaller waypoints, matched via AethernetShardName
    public bool  ShowAetheryteIcons        { get; set; } = true;
    public string  AethernetShardName   { get; set; } = "Aethernet";   // substring match, case-insensitive
    public int     AetheryteIconId      { get; set; } = 60453;
    public int     AethernetShardIconId { get; set; } = 60430;
    public Vector4 AetheryteColor       { get; set; } = new(0.55f, 0.85f, 0.95f, 0.92f);
    public float   AetheryteIconMinSize { get; set; } = 20f;
    public float   AetheryteIconMaxSize { get; set; } = 30f;
    public float   MaxMarkerDistance    { get; set; } = 100f;   // max detection range in yalms (true 3D distance)

    // Dot distance-fade curve (fractions of max range, 0–1)
    public float DotNearZone { get; set; } = 0.85f;
    public float DotFarZone  { get; set; } = 0.25f;
    public float DotMidAlpha { get; set; } = 0.50f;

    // Marker colors
    public Vector4 PlayerColor    { get; set; } = new(0.40f, 0.65f, 1.00f, 0.92f);
    public Vector4 EnemyColor     { get; set; } = new(1.00f, 0.25f, 0.25f, 0.92f);
    public Vector4 NpcColor       { get; set; } = new(0.95f, 0.88f, 0.35f, 0.92f);
    public Vector4 GatheringColor { get; set; } = new(0.30f, 0.92f, 0.40f, 0.92f);
    public Vector4 TreasureColor  { get; set; } = new(1.00f, 0.80f, 0.15f, 0.95f);

    // FATEs — zone-wide POI, independent of ShowAnyMarkers (often wanted with everything else off)
    public bool    ShowFates              { get; set; } = true;
    public Vector4 FateColor              { get; set; } = new(0.82f, 0.35f, 0.95f, 0.95f);   // fallback if icon fails to load
    public float   FateDistanceMultiplier { get; set; } = 2.5f;   // FATE range = MaxMarkerDistance × this
    public float   FateIconMinSize        { get; set; } = 20f;
    public float   FateIconMaxSize        { get; set; } = 32f;

    // True if any marker type is enabled — skips the object-table loop entirely otherwise
    public bool ShowAnyMarkers =>
        ShowPlayers || ShowEnemies || ShowNpcs || ShowGatheringNodes || ShowTreasure || ShowAetherytes;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
